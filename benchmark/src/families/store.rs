//! Family behavior over Engine transactions.
//!
//! Every read rides a never-committed snapshot transaction begun from the
//! caller's [`FamilyReadContext`], and every write is staged inside the
//! caller's transaction so the Engine publishes one marker for documents and
//! families together. Family code never commits on its own, never touches
//! storage bytes, and never inspects another seam's state.
//!
//! Document-bound family writes share one guard order: a recorded family
//! tombstone that is newer than the writer's observed document version
//! rejects the write first, then document presence, then the collection's
//! schema-validation policy, then the expected document version. A request
//! that fails any rule stages nothing, so the caller's transaction stays
//! clean and an abort or a later failure hides every family change.

use std::sync::atomic::{AtomicU64, Ordering};

use super::payloads::{
    ArchivalCommand, ArchivalReadRequest, ArchivalReadResult, ArchivalState, ArchivalWrite,
    AttachmentMetadata, AttachmentMetadataResult, AttachmentMutation, AttachmentRead,
    AttachmentWrite, CompareExchangeCondition, CompareExchangeEntry, CompareExchangeRead,
    CompareExchangeValueResult, CompareExchangeWrite, CounterBatchWrite, CounterEntry,
    CounterGroupResult, CounterOperation, CounterRead, IdentityCommand, IdentityEntry,
    IdentityRangeResult, IdentityRead, IdentityWrite, LifecycleCommand, LifecycleRead,
    LifecycleResult, LifecycleWrite, MAX_IDENTITY_VALUE, RevisionCommand, RevisionEntry,
    RevisionPage, RevisionRead, RevisionWrite, RollupPolicy, SchemaDefinition, TimeSeriesAppend,
    TimeSeriesBatchWrite, TimeSeriesDelete, TimeSeriesEntry, TimeSeriesPage, TimeSeriesPolicy,
    TimeSeriesRead, TombstoneCommand, TombstoneEntry, TombstoneRead, TombstoneResult,
    TombstoneWrite, ValidationRead, ValidationResult, ValidationWrite,
};
use super::schemas::{validate_schema, violations};
use super::state::{
    ArchivalRecord, AttachmentEntry as StoredAttachment, AttachmentRecord, CompareExchangeRecord,
    CounterRecord, IdentityRecord, LifecycleRecord, RESERVED_COLLECTION, Records, RevisionRecord,
    TimeSeriesRecord, TombstoneRecord, ValidationRecord, family_key, now_millis, record_id,
};
use super::{FamilyError, FamilyKind, FamilyReadRequest, FamilyReadResult, FamilyWriteRequest};

use crate::{
    engine::Transaction,
    types::{CollectionName, DocumentId, DocumentValue, ExpectedVersion, OperationId},
};

// ---------------------------------------------------------------------------
// Dispatch
// ---------------------------------------------------------------------------

/// Reads one family capability through a snapshot transaction.
pub(crate) fn read(
    transaction: &mut Transaction<'_>,
    request: FamilyReadRequest,
) -> Result<FamilyReadResult, FamilyError> {
    match request {
        FamilyReadRequest::AttachmentMetadata(read) => {
            let mut records = Records::new(transaction);
            attachments_read(&mut records, &read).map(FamilyReadResult::AttachmentMetadata)
        }
        FamilyReadRequest::CounterGroup(read) => {
            let mut records = Records::new(transaction);
            counters_read(&mut records, &read).map(FamilyReadResult::CounterGroup)
        }
        FamilyReadRequest::TimeSeriesRange(read) => {
            let mut records = Records::new(transaction);
            time_series_read(&mut records, &read).map(FamilyReadResult::TimeSeriesPage)
        }
        FamilyReadRequest::RevisionPage(read) => {
            let mut records = Records::new(transaction);
            revisions_read(&mut records, &read).map(FamilyReadResult::RevisionPage)
        }
        FamilyReadRequest::CompareExchangeValue(read) => {
            let mut records = Records::new(transaction);
            compare_exchange_read(&mut records, &read).map(FamilyReadResult::CompareExchangeValue)
        }
        FamilyReadRequest::IdentityRange(read) => {
            let mut records = Records::new(transaction);
            identities_read(&mut records, &read).map(FamilyReadResult::IdentityRange)
        }
        FamilyReadRequest::Lifecycle(read) => {
            let mut records = Records::new(transaction);
            lifecycle_read(&mut records, &read).map(FamilyReadResult::Lifecycle)
        }
        FamilyReadRequest::Archival(read) => {
            let mut records = Records::new(transaction);
            archival_read(&mut records, &read).map(FamilyReadResult::Archival)
        }
        FamilyReadRequest::Validation(read) => {
            let mut records = Records::new(transaction);
            validation_read(&mut records, &read).map(FamilyReadResult::Validation)
        }
        FamilyReadRequest::Tombstones(read) => {
            let mut records = Records::new(transaction);
            tombstones_read(&mut records, &read).map(FamilyReadResult::Tombstones)
        }
    }
}

/// Stages one family mutation inside the caller's transaction.
pub(crate) fn stage(
    transaction: &mut Transaction<'_>,
    request: FamilyWriteRequest,
) -> Result<(), FamilyError> {
    match request {
        FamilyWriteRequest::AttachmentMutation(write) => {
            let mut records = Records::new(transaction);
            attachments_stage(&mut records, &write)
        }
        FamilyWriteRequest::CounterBatch(write) => {
            let mut records = Records::new(transaction);
            counters_stage(&mut records, &write)
        }
        FamilyWriteRequest::TimeSeriesBatch(write) => {
            let mut records = Records::new(transaction);
            time_series_stage(&mut records, &write)
        }
        FamilyWriteRequest::RevisionCommand(write) => {
            let mut records = Records::new(transaction);
            revisions_stage(&mut records, &write)
        }
        FamilyWriteRequest::CompareExchangeCondition(write) => {
            let mut records = Records::new(transaction);
            compare_exchange_stage(&mut records, &write)
        }
        FamilyWriteRequest::IdentityAllocation(write) => {
            let mut records = Records::new(transaction);
            identities_stage(&mut records, &write)
        }
        FamilyWriteRequest::LifecycleUpdate(write) => {
            let mut records = Records::new(transaction);
            lifecycle_stage(&mut records, &write)
        }
        FamilyWriteRequest::ArchivalUpdate(write) => {
            let mut records = Records::new(transaction);
            archival_stage(&mut records, &write)
        }
        FamilyWriteRequest::SchemaValidationUpdate(write) => {
            let mut records = Records::new(transaction);
            validation_stage(&mut records, &write)
        }
        FamilyWriteRequest::TombstoneOperation(write) => {
            let mut records = Records::new(transaction);
            tombstones_stage(&mut records, &write)
        }
    }
}

/// Mints a never-reused internal operation identity for a read transaction.
pub(crate) fn internal_read_operation() -> Result<OperationId, FamilyError> {
    static COUNTER: AtomicU64 = AtomicU64::new(0);
    let index = COUNTER.fetch_add(1, Ordering::Relaxed);
    OperationId::try_new(&format!("family-read-{}-{index}", std::process::id()))
        .map_err(|_| FamilyError::StorageFailure)
}

// ---------------------------------------------------------------------------
// Shared guards
// ---------------------------------------------------------------------------

/// Returns the family kind of one reserved tombstone key.
fn family_from_key(key: &str) -> Option<FamilyKind> {
    [
        FamilyKind::Attachments,
        FamilyKind::Counters,
        FamilyKind::TimeSeries,
        FamilyKind::Revisions,
        FamilyKind::CompareExchange,
        FamilyKind::Identities,
        FamilyKind::ExpirationRefresh,
        FamilyKind::Archival,
        FamilyKind::SchemaValidation,
        FamilyKind::ConflictTombstones,
    ]
    .into_iter()
    .find(|kind| family_key(*kind) == key)
}

/// Requires one user document to exist in the transaction's view.
fn require_document(
    records: &mut Records<'_, '_>,
    document: &DocumentId,
) -> Result<(CollectionName, u64, DocumentValue), FamilyError> {
    records.user_document(document)?.ok_or(FamilyError::DocumentMissing)
}

/// Applies the shared document-bound write guard in one fixed order.
fn guard_document(
    records: &mut Records<'_, '_>,
    family: FamilyKind,
    document: &DocumentId,
    expected: ExpectedVersion,
) -> Result<(CollectionName, u64, DocumentValue), FamilyError> {
    let observed = records.user_document(document)?;
    let observed_version = observed.as_ref().map_or(0, |(_, version, _)| *version);
    let tombstone_id = record_id("tombstones", &[document.as_str()])?;
    let blocked = match records.read(&tombstone_id)? {
        None => false,
        Some(value) => TombstoneRecord::decode(&value)?
            .deletion_version(family)
            .is_some_and(|version| version > observed_version),
    };
    if blocked {
        return Err(FamilyError::TombstoneConflict);
    }
    let (collection, version, value) = observed.ok_or(FamilyError::DocumentMissing)?;
    let policy_id = record_id("validation", &[collection.as_str()])?;
    if let Some(value_record) = records.read(&policy_id)? {
        let schema = ValidationRecord::decode(&value_record)?.schema;
        if !violations(&schema, &value).is_empty() {
            return Err(FamilyError::SchemaViolation);
        }
    }
    match expected {
        ExpectedVersion::Any => {}
        // The document exists, so a `New` expectation can never hold and an
        // exact expectation must equal the observed version.
        ExpectedVersion::Exact(wanted) if wanted == version => {}
        ExpectedVersion::New | ExpectedVersion::Exact(_) => return Err(FamilyError::Conflict),
    }
    Ok((collection, version, value))
}

/// Returns one collection's configured schema-validation policy.
fn configured_schema(
    records: &mut Records<'_, '_>,
    collection: &CollectionName,
) -> Result<Option<SchemaDefinition>, FamilyError> {
    let id = record_id("validation", &[collection.as_str()])?;
    match records.read(&id)? {
        None => Ok(None),
        Some(value) => Ok(Some(ValidationRecord::decode(&value)?.schema)),
    }
}

/// Writes one record, or clears it when it holds no state.
fn write_or_clear(
    records: &mut Records<'_, '_>,
    id: DocumentId,
    empty: bool,
    encode: DocumentValue,
) -> Result<(), FamilyError> {
    if empty { records.clear(&id) } else { records.write(id, encode) }
}

// ---------------------------------------------------------------------------
// Attachments
// ---------------------------------------------------------------------------

fn attachments_read(
    records: &mut Records<'_, '_>,
    request: &AttachmentRead,
) -> Result<AttachmentMetadataResult, FamilyError> {
    let document = request.document().clone();
    require_document(records, &document)?;
    let id = record_id("attachments", &[document.as_str()])?;
    let record = match records.read(&id)? {
        Some(value) => AttachmentRecord::decode(&value)?,
        None => AttachmentRecord::default(),
    };
    let entries = record
        .entries
        .iter()
        .map(|(name, entry)| {
            AttachmentMetadata::new(
                name.clone(),
                entry.content_type.clone(),
                entry.size,
                entry.hash.clone(),
                document.clone(),
            )
        })
        .collect();
    Ok(AttachmentMetadataResult::new(entries))
}

fn attachments_stage(
    records: &mut Records<'_, '_>,
    write: &AttachmentWrite,
) -> Result<(), FamilyError> {
    let document = write.document().clone();
    guard_document(records, FamilyKind::Attachments, &document, write.expected())?;
    let id = record_id("attachments", &[document.as_str()])?;
    let mut record = match records.read(&id)? {
        Some(value) => AttachmentRecord::decode(&value)?,
        None => AttachmentRecord::default(),
    };
    match write.mutation() {
        AttachmentMutation::Store { name, content_type, size, hash, replace } => {
            if record.entries.contains_key(name) && !replace {
                return Err(FamilyError::AttachmentNameDuplicate);
            }
            record.entries.insert(
                name.clone(),
                StoredAttachment {
                    content_type: content_type.clone(),
                    size: *size,
                    hash: hash.clone(),
                },
            );
        }
        AttachmentMutation::Delete { name } => {
            record.entries.remove(name);
        }
        AttachmentMutation::Rename { from, to } => {
            let Some(entry) = record.entries.remove(from) else {
                return Err(FamilyError::InvalidRequest);
            };
            if record.entries.contains_key(to) {
                return Err(FamilyError::AttachmentNameDuplicate);
            }
            record.entries.insert(to.clone(), entry);
        }
        AttachmentMutation::Copy { name, to_document, to_name }
        | AttachmentMutation::Move { name, to_document, to_name } => {
            let moving = matches!(write.mutation(), AttachmentMutation::Move { .. });
            let Some(entry) = record.entries.get(name).cloned() else {
                return Err(FamilyError::InvalidRequest);
            };
            if to_document == &document {
                // A same-document copy or move is ONE record mutation: the
                // Engine rejects a write set that stages the same document
                // identifier twice, so the target write and the source
                // mutation merge here instead of staging two puts.
                if record.entries.contains_key(to_name) {
                    return Err(FamilyError::AttachmentNameDuplicate);
                }
                if moving {
                    record.entries.remove(name);
                }
                record.entries.insert(to_name.clone(), entry);
            } else {
                guard_document(
                    records,
                    FamilyKind::Attachments,
                    to_document,
                    ExpectedVersion::Any,
                )?;
                let target_id = record_id("attachments", &[to_document.as_str()])?;
                let mut target = match records.read(&target_id)? {
                    Some(value) => AttachmentRecord::decode(&value)?,
                    None => AttachmentRecord::default(),
                };
                if target.entries.contains_key(to_name) {
                    return Err(FamilyError::AttachmentNameDuplicate);
                }
                target.entries.insert(
                    to_name.clone(),
                    StoredAttachment {
                        content_type: entry.content_type.clone(),
                        size: entry.size,
                        hash: entry.hash.clone(),
                    },
                );
                records.write(target_id, target.encode())?;
                if moving {
                    record.entries.remove(name);
                }
            }
        }
    }
    write_or_clear(records, id, record.entries.is_empty(), record.encode())
}

// ---------------------------------------------------------------------------
// Counters
// ---------------------------------------------------------------------------

fn counters_read(
    records: &mut Records<'_, '_>,
    request: &CounterRead,
) -> Result<CounterGroupResult, FamilyError> {
    let document = request.document().clone();
    require_document(records, &document)?;
    let id = record_id("counters", &[document.as_str()])?;
    let record = match records.read(&id)? {
        Some(value) => CounterRecord::decode(&value)?,
        None => CounterRecord::default(),
    };
    let entries = if request.names().is_empty() {
        record
            .counters
            .iter()
            .map(|(name, value)| CounterEntry::new(name.clone(), Some(*value)))
            .collect()
    } else {
        request
            .names()
            .iter()
            .map(|name| CounterEntry::new(name.clone(), record.counters.get(name).copied()))
            .collect()
    };
    Ok(CounterGroupResult::new(entries))
}

fn counters_stage(
    records: &mut Records<'_, '_>,
    write: &CounterBatchWrite,
) -> Result<(), FamilyError> {
    let document = write.document().clone();
    guard_document(records, FamilyKind::Counters, &document, write.expected())?;
    let id = record_id("counters", &[document.as_str()])?;
    let mut record = match records.read(&id)? {
        Some(value) => CounterRecord::decode(&value)?,
        None => CounterRecord::default(),
    };
    for operation in write.operations() {
        match operation {
            CounterOperation::Increment { name, delta } => {
                let current = record.counters.get(name).copied().unwrap_or(0);
                let Some(next) = current.checked_add(*delta) else {
                    return Err(FamilyError::CounterOverflow);
                };
                record.counters.insert(name.clone(), next);
            }
            CounterOperation::Delete { name } => {
                record.counters.remove(name);
            }
        }
    }
    write_or_clear(records, id, record.counters.is_empty(), record.encode())
}

// ---------------------------------------------------------------------------
// Time series
// ---------------------------------------------------------------------------

fn time_series_read(
    records: &mut Records<'_, '_>,
    request: &TimeSeriesRead,
) -> Result<TimeSeriesPage, FamilyError> {
    let document = request.document().clone();
    require_document(records, &document)?;
    let id = record_id("time-series", &[document.as_str(), request.name()])?;
    let record = match records.read(&id)? {
        Some(value) => TimeSeriesRecord::decode(&value)?,
        None => TimeSeriesRecord::default(),
    };
    // The retention policy bounds both read paths symmetrically: a ranged
    // read whose entire range precedes the horizon is a typed range error,
    // and a full read returns the retained view only. The horizon is derived
    // from the newest stored point, because retention cleanup itself belongs
    // to the streaming packet.
    let horizon =
        record.policy.as_ref().and_then(TimeSeriesPolicy::retention_ms).and_then(|retention| {
            record.entries.last().map(|newest| newest.timestamp().saturating_sub(retention))
        });
    if let (Some(horizon), Some(range)) = (horizon, request.range())
        && range.end() < horizon
    {
        return Err(FamilyError::TimeSeriesRangeInvalid);
    }
    let entries = record
        .entries
        .iter()
        .filter(|entry| match request.range() {
            Some(range) => entry.timestamp() >= range.start() && entry.timestamp() <= range.end(),
            None => horizon.is_none_or(|bound| entry.timestamp() >= bound),
        })
        .map(|entry| {
            TimeSeriesEntry::new(
                entry.timestamp(),
                entry.values().to_vec(),
                entry.tag().map(str::to_owned),
            )
        })
        .collect();
    Ok(TimeSeriesPage::new(entries, record.policy.clone()))
}

fn validate_policy(policy: &TimeSeriesPolicy) -> Result<(), FamilyError> {
    if policy.retention_ms().is_some_and(|value| value < 0) {
        return Err(FamilyError::TimeSeriesPolicyInvalid);
    }
    let mut names: Vec<&str> = policy.rollups().iter().map(RollupPolicy::name).collect();
    names.sort_unstable();
    if names.windows(2).any(|pair| pair[0] == pair[1]) {
        return Err(FamilyError::TimeSeriesPolicyInvalid);
    }
    if policy.rollups().iter().any(|rollup| rollup.window_ms() <= 0) {
        return Err(FamilyError::TimeSeriesPolicyInvalid);
    }
    Ok(())
}

fn time_series_stage(
    records: &mut Records<'_, '_>,
    write: &TimeSeriesBatchWrite,
) -> Result<(), FamilyError> {
    let document = write.document().clone();
    guard_document(records, FamilyKind::TimeSeries, &document, write.expected())?;
    let id = record_id("time-series", &[document.as_str(), write.name()])?;
    let mut record = match records.read(&id)? {
        Some(value) => TimeSeriesRecord::decode(&value)?,
        None => TimeSeriesRecord::default(),
    };
    if let Some(policy) = write.policy() {
        validate_policy(policy)?;
        record.policy = Some(policy.clone());
    }
    for deletion in write.deletes() {
        match *deletion {
            TimeSeriesDelete::At { timestamp } => record.delete_at(timestamp),
            TimeSeriesDelete::Range { start, end } => {
                if start > end {
                    return Err(FamilyError::TimeSeriesRangeInvalid);
                }
                record.delete_range(start, end);
            }
        }
    }
    let arity =
        record.arity.or_else(|| write.appends().first().map(|append| append.values().len()));
    for append in write.appends() {
        if append.values().is_empty() {
            return Err(FamilyError::TimeSeriesArityInvalid);
        }
        if Some(append.values().len()) != arity {
            return Err(FamilyError::TimeSeriesArityInvalid);
        }
        record.entries.retain(|entry| entry.timestamp() != append.timestamp());
        record.entries.push(TimeSeriesAppend::new(
            append.timestamp(),
            append.values().to_vec(),
            append.tag().map(str::to_owned),
        ));
    }
    record.entries.sort_by_key(TimeSeriesAppend::timestamp);
    record.arity = arity;
    let empty = record.entries.is_empty() && record.policy.is_none();
    write_or_clear(records, id, empty, record.encode())
}

// ---------------------------------------------------------------------------
// Revisions
// ---------------------------------------------------------------------------

fn revisions_read(
    records: &mut Records<'_, '_>,
    request: &RevisionRead,
) -> Result<RevisionPage, FamilyError> {
    let document = request.document().clone();
    let id = record_id("revisions", &[document.as_str()])?;
    let record = match records.read(&id)? {
        Some(value) => RevisionRecord::decode(&value)?,
        None => RevisionRecord::default(),
    };
    let total = u64::try_from(record.entries.len()).unwrap_or(u64::MAX);
    let start = usize::try_from(request.paging().offset()).unwrap_or(usize::MAX);
    let limit = usize::try_from(request.paging().limit()).unwrap_or(usize::MAX);
    let entries = record
        .entries
        .iter()
        .skip(start)
        .take(limit)
        .map(|(revision, at, value)| RevisionEntry::new(*revision, *at, value.clone()))
        .collect();
    Ok(RevisionPage::new(entries, total, record.policy))
}

fn revisions_stage(
    records: &mut Records<'_, '_>,
    write: &RevisionWrite,
) -> Result<(), FamilyError> {
    let document = write.document().clone();
    let id = record_id("revisions", &[document.as_str()])?;
    let mut record = match records.read(&id)? {
        Some(value) => RevisionRecord::decode(&value)?,
        None => RevisionRecord::default(),
    };
    match write.command() {
        RevisionCommand::ForceCreate { expected } => {
            let (_, _, value) =
                guard_document(records, FamilyKind::Revisions, &document, *expected)?;
            record.push(now_millis(), value);
        }
        RevisionCommand::Revert { to_revision } => {
            let (collection, _, _) =
                guard_document(records, FamilyKind::Revisions, &document, ExpectedVersion::Any)?;
            let Some((_, _, restored)) =
                record.entries.iter().find(|(revision, _, _)| *revision == *to_revision)
            else {
                return Err(FamilyError::RevisionReferenceInvalid);
            };
            let restored = restored.clone();
            record.push(now_millis(), restored.clone());
            records.write_user_document(document.clone(), collection, restored)?;
        }
        RevisionCommand::Configure { policy } => {
            record.policy = Some(*policy);
            record.trim_to_policy();
        }
    }
    write_or_clear(
        records,
        id,
        record.entries.is_empty() && record.policy.is_none(),
        record.encode(),
    )
}

// ---------------------------------------------------------------------------
// Compare exchange
// ---------------------------------------------------------------------------

fn compare_exchange_read(
    records: &mut Records<'_, '_>,
    request: &CompareExchangeRead,
) -> Result<CompareExchangeValueResult, FamilyError> {
    let id = record_id("compare-exchange", &[request.key()])?;
    let now = now_millis();
    let entry = match records.read(&id)? {
        Some(value) => {
            let record = CompareExchangeRecord::decode(&value)?;
            record.is_live(now).then(|| {
                CompareExchangeEntry::new(
                    record.value.clone().unwrap_or(DocumentValue::Null),
                    record.index,
                    record.expires_at,
                )
            })
        }
        None => None,
    };
    Ok(CompareExchangeValueResult::new(request.key().to_owned(), entry))
}

fn compare_exchange_stage(
    records: &mut Records<'_, '_>,
    write: &CompareExchangeWrite,
) -> Result<(), FamilyError> {
    let id = record_id("compare-exchange", &[write.key()])?;
    let now = now_millis();
    let mut record = match records.read(&id)? {
        Some(value) => CompareExchangeRecord::decode(&value)?,
        None => CompareExchangeRecord::default(),
    };
    match write.condition() {
        CompareExchangeCondition::PutIfIndex { index, value, expires_at } => {
            if *index != record.index {
                return Err(FamilyError::CompareExchangeIndexStale);
            }
            record.index = record.index.saturating_add(1);
            record.value = Some(value.clone());
            record.expires_at = *expires_at;
            record.deleted = false;
        }
        CompareExchangeCondition::PutIfMissing { value, expires_at } => {
            if record.is_live(now) {
                return Err(FamilyError::CompareExchangeIndexStale);
            }
            record.index = record.index.saturating_add(1);
            record.value = Some(value.clone());
            record.expires_at = *expires_at;
            record.deleted = false;
        }
        CompareExchangeCondition::DeleteIfIndex { index } => {
            if *index != record.index || !record.is_live(now) {
                return Err(FamilyError::CompareExchangeIndexStale);
            }
            record.index = record.index.saturating_add(1);
            record.value = None;
            record.expires_at = None;
            record.deleted = true;
        }
    }
    records.write(id, record.encode())
}

// ---------------------------------------------------------------------------
// Identities
// ---------------------------------------------------------------------------

fn identities_read(
    records: &mut Records<'_, '_>,
    request: &IdentityRead,
) -> Result<IdentityRangeResult, FamilyError> {
    let mut entries = Vec::new();
    for name in request.names() {
        let id = record_id("identities", &[name])?;
        let next = match records.read(&id)? {
            Some(value) => IdentityRecord::decode(&value)?.next,
            None => 0,
        };
        entries.push(IdentityEntry::new(name.clone(), next));
    }
    Ok(IdentityRangeResult::new(entries))
}

fn identities_stage(
    records: &mut Records<'_, '_>,
    write: &IdentityWrite,
) -> Result<(), FamilyError> {
    let id = record_id("identities", &[write.name()])?;
    let mut record = match records.read(&id)? {
        Some(value) => IdentityRecord::decode(&value)?,
        None => IdentityRecord::default(),
    };
    match write.command() {
        IdentityCommand::Allocate { count, .. } => {
            // Saturating would pin `next` at a ceiling forever; allocation
            // past the representable identity range is a typed failure,
            // consistent with counter overflow.
            let Some(sum) = record.next.checked_add(*count) else {
                return Err(FamilyError::IdentityExhausted);
            };
            if sum > MAX_IDENTITY_VALUE {
                return Err(FamilyError::IdentityExhausted);
            }
            record.next = sum;
        }
        IdentityCommand::Seed { value, force, .. } => {
            if *value < record.next && !force {
                return Err(FamilyError::IdentitySeedInvalid);
            }
            record.next = *value;
        }
    }
    records.write(id, record.encode())
}

// ---------------------------------------------------------------------------
// Lifecycle
// ---------------------------------------------------------------------------

fn lifecycle_read(
    records: &mut Records<'_, '_>,
    request: &LifecycleRead,
) -> Result<LifecycleResult, FamilyError> {
    let document = request.document().clone();
    let present = records.user_document(&document)?.is_some();
    let id = record_id("lifecycle", &[document.as_str()])?;
    let record = match records.read(&id)? {
        Some(value) => LifecycleRecord::decode(&value)?,
        None => LifecycleRecord::default(),
    };
    let expired = present && record.is_expired(now_millis());
    Ok(LifecycleResult::new(document, present, record.expires_at, record.refresh_at, expired))
}

fn lifecycle_stage(
    records: &mut Records<'_, '_>,
    write: &LifecycleWrite,
) -> Result<(), FamilyError> {
    let document = write.document().clone();
    guard_document(records, FamilyKind::ExpirationRefresh, &document, write.expected())?;
    let id = record_id("lifecycle", &[document.as_str()])?;
    let mut record = match records.read(&id)? {
        Some(value) => LifecycleRecord::decode(&value)?,
        None => LifecycleRecord::default(),
    };
    match write.command() {
        LifecycleCommand::SetExpiration { at } => {
            if at.is_some_and(|value| value < 0) {
                return Err(FamilyError::LifecycleValueInvalid);
            }
            record.expires_at = at;
        }
        LifecycleCommand::SetRefresh { at } => {
            if at.is_some_and(|value| value < 0) {
                return Err(FamilyError::LifecycleValueInvalid);
            }
            record.refresh_at = at;
        }
        LifecycleCommand::Clear => {
            record.expires_at = None;
            record.refresh_at = None;
        }
    }
    write_or_clear(
        records,
        id,
        record.expires_at.is_none() && record.refresh_at.is_none(),
        record.encode(),
    )
}

// ---------------------------------------------------------------------------
// Archival
// ---------------------------------------------------------------------------

fn archival_read(
    records: &mut Records<'_, '_>,
    request: &ArchivalReadRequest,
) -> Result<ArchivalReadResult, FamilyError> {
    let document = request.document().clone();
    let present = records.user_document(&document)?.is_some();
    let id = record_id("archival", &[document.as_str()])?;
    let state = match records.read(&id)? {
        Some(value) => {
            let record = ArchivalRecord::decode(&value)?;
            Some(ArchivalState::new(record.archived, record.at, record.after_ms))
        }
        None => None,
    };
    if !present && state.is_none() {
        return Err(FamilyError::DocumentMissing);
    }
    Ok(ArchivalReadResult::new(document, state))
}

fn archival_stage(records: &mut Records<'_, '_>, write: &ArchivalWrite) -> Result<(), FamilyError> {
    let document = write.document().clone();
    guard_document(records, FamilyKind::Archival, &document, ExpectedVersion::Any)?;
    let id = record_id("archival", &[document.as_str()])?;
    let mut record = match records.read(&id)? {
        Some(value) => ArchivalRecord::decode(&value)?,
        None => ArchivalRecord::default(),
    };
    match write.command() {
        ArchivalCommand::SetPolicy { after_ms } => {
            if after_ms < 0 {
                return Err(FamilyError::InvalidRequest);
            }
            record.after_ms = Some(after_ms);
        }
        ArchivalCommand::Archive { at } => {
            if at < 0 {
                return Err(FamilyError::InvalidRequest);
            }
            record.archived = true;
            record.at = Some(at);
        }
        ArchivalCommand::Restore => {
            record.archived = false;
            record.at = None;
        }
    }
    write_or_clear(
        records,
        id,
        record.archived && record.at.is_none() && record.after_ms.is_none(),
        record.encode(),
    )
}

// ---------------------------------------------------------------------------
// Schema validation
// ---------------------------------------------------------------------------

fn validation_read(
    records: &mut Records<'_, '_>,
    request: &ValidationRead,
) -> Result<ValidationResult, FamilyError> {
    let collection = request.collection().clone();
    let policy = configured_schema(records, &collection)?;
    let mut found = Vec::new();
    if let (Some(document), Some(schema)) = (request.document(), policy.as_ref()) {
        let (_, _, value) = require_document(records, document)?;
        found = violations(schema, &value);
    }
    Ok(ValidationResult::new(collection, policy, found))
}

/// Stages one schema declaration or clear for one collection.
///
/// A declaration is refused on the reserved family collection (family state
/// is engine state, and a schema on the schema store itself would gate its
/// own bootstrap), and a recorded schema evolves only forward: the same
/// version with the same rules restates idempotently, a greater version
/// declares the next schema, and anything else conflicts with the recorded
/// version. The read observes the transaction's staged writes, so two
/// declarations inside one transaction check against each other.
fn validation_stage(
    records: &mut Records<'_, '_>,
    write: &ValidationWrite,
) -> Result<(), FamilyError> {
    if write.collection().as_str() == RESERVED_COLLECTION {
        return Err(FamilyError::InvalidRequest);
    }
    let id = record_id("validation", &[write.collection().as_str()])?;
    match write.schema() {
        None => records.clear(&id),
        Some(schema) => {
            validate_schema(schema)?;
            if let Some(recorded) = configured_schema(records, write.collection())? {
                let evolves = schema.version() > recorded.version() || *schema == recorded;
                if !evolves {
                    return Err(FamilyError::Conflict);
                }
            }
            let record = ValidationRecord { schema: schema.clone() };
            records.write(id, record.encode())
        }
    }
}

// ---------------------------------------------------------------------------
// Tombstones
// ---------------------------------------------------------------------------

fn tombstones_read(
    records: &mut Records<'_, '_>,
    request: &TombstoneRead,
) -> Result<TombstoneResult, FamilyError> {
    let document = request.document().clone();
    let id = record_id("tombstones", &[document.as_str()])?;
    let record = match records.read(&id)? {
        Some(value) => TombstoneRecord::decode(&value)?,
        None => TombstoneRecord::default(),
    };
    let entries = record
        .entries
        .iter()
        .filter_map(|(key, (version, recorded_at))| {
            family_from_key(key).map(|family| TombstoneEntry::new(family, *version, *recorded_at))
        })
        .collect();
    Ok(TombstoneResult::new(document, entries))
}

fn tombstones_stage(
    records: &mut Records<'_, '_>,
    write: &TombstoneWrite,
) -> Result<(), FamilyError> {
    let document = write.document().clone();
    let id = record_id("tombstones", &[document.as_str()])?;
    let mut record = match records.read(&id)? {
        Some(value) => TombstoneRecord::decode(&value)?,
        None => TombstoneRecord::default(),
    };
    match write.command() {
        TombstoneCommand::Record { family, deletion_version } => {
            record.entries.insert(family_key(family).to_owned(), (deletion_version, now_millis()));
        }
        TombstoneCommand::Clear { family } => {
            record.entries.remove(family_key(family));
        }
    }
    write_or_clear(records, id, record.entries.is_empty(), record.encode())
}
