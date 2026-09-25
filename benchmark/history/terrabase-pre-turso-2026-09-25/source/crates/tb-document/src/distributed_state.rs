//! Bounded distributed state traversal and retained-frame audit.

use std::collections::BTreeMap;

use tb_core::{Attribution, Key, MemoryBudget, MemoryCategory, ReadSnapshot, Reservation};
use tb_format::{
    DocumentDot, DocumentKey, DocumentKind, SiblingDescriptor, WalOperation,
    decode_applied_identity, decode_catalog, decode_causal_manifest, decode_change,
    decode_document, decode_document_key, decode_high_water_value, decode_retry_value,
    decode_state_pointer, describe_change, descriptor_change_key, encode_catalog_key,
    encode_causal_manifest_into, encode_causal_manifest_key, encode_high_water_key,
    encode_marker_key, encode_primary_key, encode_retry_key, validate_document_schema,
    verify_descriptor_change, verify_state_pointer_manifest,
};
use tb_replication::{
    CausalContext, CertifiedShardView, CollectionBinding, ReplicationLimits, WalRole,
    compute_event_digest, decode_metadata, verify_incoming,
};
use tb_storage::{CommittedBatch, ScanRow, Storage, StorageConfig};

use crate::distributed::scan_all_visible;
use crate::{DocumentError, key};

pub(crate) fn effective_limits(mut limits: ReplicationLimits) -> ReplicationLimits {
    limits.max_origins = limits.max_origins.min(64);
    limits.max_siblings = limits.max_siblings.min(64);
    limits
}

/// Merge only bounded D1 descriptors; prior sibling bodies remain in storage.
#[allow(clippy::too_many_arguments)]
pub(crate) fn merge_manifest(
    storage: &Storage,
    budget: &MemoryBudget,
    config: &StorageConfig,
    view: &CertifiedShardView,
    limits: ReplicationLimits,
    snapshot: ReadSnapshot,
    collection: tb_core::CollectionId,
    document: tb_core::DocumentId,
    observed: &CausalContext,
    source_key: &[u8],
    source_value: &[u8],
) -> Result<(Vec<u8>, [u8; 32]), DocumentError> {
    let primary_key = crate::key(encode_primary_key(collection, document))?;
    let mut context = BTreeMap::<tb_core::OriginId, u64>::new();
    let mut siblings = Vec::<SiblingDescriptor>::new();
    if let Some(value) = storage.get(snapshot, &primary_key)? {
        let (manifest, _permit) = load_manifest(
            storage,
            budget,
            config,
            view,
            limits,
            snapshot,
            (primary_key.as_bytes(), value.bytes()),
        )?;
        for dot in manifest.context() {
            context.insert(dot.origin, dot.sequence);
        }
        for &sibling in manifest.siblings() {
            if observed.get(sibling.event.origin) < sibling.event.sequence {
                siblings.push(sibling);
            }
        }
    }
    for (origin, prefix) in observed.entries() {
        context
            .entry(origin)
            .and_modify(|old| *old = (*old).max(prefix))
            .or_insert(prefix);
    }
    let new_sibling = describe_change(source_key, source_value)?;
    let event = new_sibling.event;
    context
        .entry(event.origin)
        .and_modify(|old| *old = (*old).max(event.sequence))
        .or_insert(event.sequence);
    siblings.push(new_sibling);
    siblings.sort_by_key(|sibling| sibling.event);
    let context = context
        .into_iter()
        .map(|(origin, sequence)| DocumentDot { origin, sequence })
        .collect::<Vec<_>>();
    let mut encoded = Vec::new();
    let sha = encode_causal_manifest_into(
        &mut encoded,
        collection,
        document,
        event.database,
        event.shard,
        &context,
        &siblings,
        limits.max_origins,
        limits.max_siblings,
    )?;
    Ok((encoded, sha))
}

fn query_attribution(config: &StorageConfig) -> Attribution {
    Attribution {
        tenant: config.tenant,
        worker: config.worker,
        category: MemoryCategory::Query,
    }
}

#[derive(Clone, Copy)]
struct AuditContext<'a> {
    storage: &'a Storage,
    config: &'a StorageConfig,
    budget: &'a MemoryBudget,
    view: &'a CertifiedShardView,
    limits: ReplicationLimits,
    snapshot: ReadSnapshot,
}

/// Document-only structural recovery. Inbox coupling is checked by the later
/// receiver runtime before it can claim applied readiness.
pub(crate) struct DistributedAudit {
    pub(crate) local_sequence: u64,
    pub(crate) applied_prefix: BTreeMap<tb_core::OriginId, u64>,
}

fn signed_catalog(
    view: &CertifiedShardView,
    collection: tb_core::CollectionId,
    generation: u64,
) -> Result<&[u8], DocumentError> {
    view.catalogs()
        .find(|(binding, _)| binding.collection == collection && binding.generation == generation)
        .map(|(_, bytes)| bytes)
        .ok_or(DocumentError::Corrupt("unsigned distributed catalog"))
}

pub(crate) fn check_signed_binding(
    view: &CertifiedShardView,
    binding: CollectionBinding,
) -> Result<&[u8], DocumentError> {
    let bytes = signed_catalog(view, binding.collection, binding.generation)?;
    if tb_format::decode_catalog(bytes)?.digest != binding.schema_digest {
        return Err(DocumentError::Corrupt("signed schema digest mismatch"));
    }
    Ok(bytes)
}

pub(crate) fn load_manifest(
    storage: &Storage,
    budget: &MemoryBudget,
    config: &StorageConfig,
    view: &CertifiedShardView,
    limits: ReplicationLimits,
    snapshot: ReadSnapshot,
    primary: (&[u8], &[u8]),
) -> Result<(tb_format::CausalManifest, Reservation), DocumentError> {
    let pointer = decode_state_pointer(primary.0, primary.1)?;
    let manifest_key = key(encode_causal_manifest_key(
        pointer.collection,
        pointer.document,
        pointer.manifest_sha256,
    ))?;
    let manifest_value = storage
        .get(snapshot, &manifest_key)?
        .ok_or(DocumentError::Corrupt("D0 references missing D6"))?;
    let decode_bytes = manifest_value
        .bytes()
        .len()
        .checked_mul(4)
        .and_then(|n| n.checked_add(4_096))
        .ok_or(DocumentError::Capacity("D6 decode bytes"))?;
    let permit = budget.try_reserve(query_attribution(config), decode_bytes)?;
    let manifest = decode_causal_manifest(
        manifest_key.as_bytes(),
        manifest_value.bytes(),
        view.database(),
        view.shard(),
        limits.max_origins,
        limits.max_siblings,
    )?;
    verify_state_pointer_manifest(pointer, &manifest)?;
    for &descriptor in manifest.siblings() {
        let change_key = key(descriptor_change_key(&manifest, descriptor)?)?;
        let value = storage
            .get(snapshot, &change_key)?
            .ok_or(DocumentError::Corrupt("D6 references missing D1"))?;
        verify_descriptor_change(&manifest, descriptor, change_key.as_bytes(), value.bytes())?;
        check_signed_binding(
            view,
            CollectionBinding {
                collection: manifest.collection(),
                generation: descriptor.generation,
                schema_digest: descriptor.schema_digest,
            },
        )?;
    }
    Ok((manifest, permit))
}

fn check_manifest_chain(
    storage: &Storage,
    budget: &MemoryBudget,
    config: &StorageConfig,
    view: &CertifiedShardView,
    limits: ReplicationLimits,
    snapshot: ReadSnapshot,
    primary: (&[u8], &[u8]),
) -> Result<(), DocumentError> {
    let (_manifest, _permit) =
        load_manifest(storage, budget, config, view, limits, snapshot, primary)?;
    Ok(())
}

fn check_pure_manifest(
    view: &CertifiedShardView,
    limits: ReplicationLimits,
    key: &[u8],
    value: &[u8],
) -> Result<(), DocumentError> {
    let manifest = decode_causal_manifest(
        key,
        value,
        view.database(),
        view.shard(),
        limits.max_origins,
        limits.max_siblings,
    )?;
    for descriptor in manifest.siblings() {
        check_signed_binding(
            view,
            CollectionBinding {
                collection: manifest.collection(),
                generation: descriptor.generation,
                schema_digest: descriptor.schema_digest,
            },
        )?;
    }
    Ok(())
}

fn audit_retained_frames(ctx: &AuditContext<'_>) -> Result<DistributedAudit, DocumentError> {
    let AuditContext {
        storage,
        config,
        view,
        limits,
        ..
    } = *ctx;
    let mut cursor = storage.committed_cursor()?;
    let mut after = tb_core::CommitSeq::ZERO;
    let mut source_sequence = 0_u64;
    let mut applied_prefix = BTreeMap::<tb_core::OriginId, u64>::new();
    while let Some(batch) = cursor.next(after, config.format.max_frame_bytes)? {
        after = batch.sequence;
        let frame = batch.frame(&config.format)?;
        let mut c0 = 0;
        let mut d6 = 0;
        let mut marker = None;
        let mut applied = None;
        for operation in frame.operations() {
            let WalOperation::Put { key, value } = operation? else {
                return Err(DocumentError::Corrupt("Delete in distributed document WAL"));
            };
            match key.first() {
                Some(0xC0) => c0 += 1,
                Some(0xD6) => d6 += 1,
                Some(0xD2) if marker.replace(value).is_some() => {
                    return Err(DocumentError::Corrupt("multiple D2 markers in event"));
                }
                Some(0xD5) if applied.replace((key, value)).is_some() => {
                    return Err(DocumentError::Corrupt("multiple D5 markers in event"));
                }
                _ => {}
            }
        }
        if c0 == frame.operation_count() {
            for operation in frame.operations() {
                let WalOperation::Put { key, value } = operation? else {
                    unreachable!("classified C0 Put frame")
                };
                let DocumentKey::Catalog {
                    collection,
                    generation,
                } = decode_document_key(key)?
                else {
                    return Err(DocumentError::Corrupt("invalid C0 stage key"));
                };
                if signed_catalog(view, collection, generation)? != value {
                    return Err(DocumentError::Corrupt("altered C0 stage row"));
                }
            }
            continue;
        }
        if d6 == frame.operation_count() {
            for operation in frame.operations() {
                let WalOperation::Put { key, value } = operation? else {
                    unreachable!("classified D6 Put frame")
                };
                check_pure_manifest(view, limits, key, value)?;
            }
            continue;
        }
        if c0 != 0 || d6 != 0 {
            return Err(DocumentError::Corrupt("mixed distributed WAL group"));
        }
        if let Some((applied_key, applied_value)) = applied {
            if marker.is_some() {
                return Err(DocumentError::Corrupt("mixed source and receiver markers"));
            }
            let identity = verify_receiver_frame(ctx, &batch, applied_key, applied_value)?;
            let previous = applied_prefix
                .get(&identity.event.origin)
                .copied()
                .unwrap_or(0);
            if previous
                .checked_add(1)
                .is_none_or(|next| identity.event.sequence != next)
            {
                return Err(DocumentError::Corrupt("receiver D5 origin gap"));
            }
            applied_prefix.insert(identity.event.origin, identity.event.sequence);
            continue;
        }
        let metadata_bytes = marker.ok_or(DocumentError::Corrupt("unclassified WAL group"))?;
        let expected_sequence = source_sequence
            .checked_add(1)
            .ok_or(DocumentError::Capacity("source sequence exhausted"))?;
        verify_retained_event(ctx, &batch, metadata_bytes, expected_sequence)?;
        source_sequence = expected_sequence;
    }
    Ok(DistributedAudit {
        local_sequence: source_sequence,
        applied_prefix,
    })
}

#[allow(
    clippy::too_many_lines,
    reason = "receiver audit checks the full retained frame and applied identity as one corruption boundary"
)]
fn verify_receiver_frame(
    ctx: &AuditContext<'_>,
    batch: &CommittedBatch,
    applied_key: &[u8],
    applied_value: &[u8],
) -> Result<tb_format::AppliedIdentity, DocumentError> {
    let AuditContext {
        storage,
        config,
        budget,
        view,
        limits,
        snapshot,
    } = *ctx;
    let identity =
        decode_applied_identity(applied_key, applied_value, view.database(), view.shard())?;
    if identity.epoch != view.epoch()
        || identity.configuration != view.configuration().to_bytes()
        || identity.event.origin == view.local_origin()
        || !view
            .members()
            .any(|member| member.origin == identity.event.origin)
    {
        return Err(DocumentError::Corrupt("receiver D5 authority mismatch"));
    }
    let marker = storage
        .get(snapshot, &Key::new(applied_key.to_vec())?)?
        .ok_or(DocumentError::Corrupt("retained receiver D5 missing"))?;
    if marker.bytes() != applied_value {
        return Err(DocumentError::Corrupt("retained receiver D5 changed"));
    }
    let frame = batch.frame(&config.format)?;
    let operations = frame.operation_count();
    if operations < 3 || operations % 2 != 1 {
        return Err(DocumentError::Corrupt("receiver final role count"));
    }
    let count = (operations - 1) / 2;
    if count > limits.max_changes {
        return Err(DocumentError::Capacity("receiver final change count"));
    }
    let audit_bytes = operations
        .checked_mul(std::mem::size_of::<(&[u8], &[u8])>())
        .ok_or(DocumentError::Capacity("receiver audit operations"))?;
    let _work = budget.try_reserve(query_attribution(config), audit_bytes)?;
    let mut puts = Vec::new();
    puts.try_reserve_exact(operations)
        .map_err(|_| DocumentError::Capacity("receiver audit operations"))?;
    for operation in frame.operations() {
        let WalOperation::Put { key, value } = operation? else {
            return Err(DocumentError::Corrupt("Delete in receiver final"));
        };
        puts.push((key, value));
    }
    if puts.last().copied() != Some((applied_key, applied_value)) {
        return Err(DocumentError::Corrupt("receiver D5 not last"));
    }
    let mut previous = None;
    for index in 0..count {
        let (change_key, change_value) = puts[index];
        let (pointer_key, pointer_value) = puts[index + count];
        let descriptor = describe_change(change_key, change_value)?;
        let change = decode_change(change_value)?;
        if descriptor.event != identity.event
            || descriptor.ordinal as usize != index
            || previous
                .is_some_and(|pair| pair >= (change.binding.collection, change.binding.document))
            || decode_document_key(pointer_key)?
                != (DocumentKey::Primary {
                    collection: change.binding.collection,
                    document: change.binding.document,
                })
        {
            return Err(DocumentError::Corrupt(
                "receiver final D1/D0 order or scope",
            ));
        }
        previous = Some((change.binding.collection, change.binding.document));
        let catalog = decode_catalog(check_signed_binding(
            view,
            CollectionBinding {
                collection: change.binding.collection,
                generation: change.binding.generation,
                schema_digest: change.binding.schema_digest,
            },
        )?)?;
        if change.kind == DocumentKind::Live {
            validate_document_schema(decode_document(change.document_bytes)?, &catalog.schema)?;
        }
        let stored = storage
            .get(snapshot, &Key::new(change_key.to_vec())?)?
            .ok_or(DocumentError::Corrupt("retained receiver D1 missing"))?;
        if stored.bytes() != change_value {
            return Err(DocumentError::Corrupt("retained receiver D1 changed"));
        }
        let (manifest, _permit) = load_manifest(
            storage,
            budget,
            config,
            view,
            limits,
            snapshot,
            (pointer_key, pointer_value),
        )?;
        if !manifest.siblings().contains(&descriptor) {
            return Err(DocumentError::Corrupt("receiver D0 omits new D1"));
        }
    }
    Ok(identity)
}

fn verify_retained_event(
    ctx: &AuditContext<'_>,
    batch: &CommittedBatch,
    metadata_bytes: &[u8],
    expected_sequence: u64,
) -> Result<(), DocumentError> {
    let AuditContext {
        storage,
        config,
        budget,
        view,
        limits,
        snapshot,
    } = *ctx;
    let frame = batch.frame(&config.format)?;
    let metadata = decode_metadata(metadata_bytes, limits)?;
    if metadata.header.record_format != 2
        || metadata.roles.contains(&WalRole::LocalIndex)
        || metadata.header.id.origin() != view.local_origin()
        || metadata.header.id.database() != view.database()
        || metadata.header.id.shard() != view.shard()
        || metadata.header.epoch != view.epoch()
        || metadata.header.configuration != view.configuration()
        || metadata.header.id.sequence() != expected_sequence
    {
        return Err(DocumentError::Corrupt(
            "distributed source event scope or order",
        ));
    }
    for &binding in &metadata.bindings {
        check_signed_binding(view, binding)?;
    }
    let digest = compute_event_digest(metadata_bytes, batch.encoded_bytes())?;
    let proof = verify_incoming(
        metadata_bytes,
        batch.encoded_bytes(),
        digest,
        limits,
        budget,
        Attribution {
            category: MemoryCategory::Replication,
            ..query_attribution(config)
        },
    )?;
    if proof.event().id() != metadata.header.id {
        return Err(DocumentError::Corrupt("verified source identity changed"));
    }
    let marker_key = key(encode_marker_key(
        view.local_origin(),
        metadata.header.id.sequence(),
    )?)?;
    if storage
        .get(snapshot, &marker_key)?
        .as_ref()
        .map(tb_storage::VersionedValue::bytes)
        != Some(metadata_bytes)
    {
        return Err(DocumentError::Corrupt(
            "retained D2 marker missing or changed",
        ));
    }
    let retry_key = key(encode_retry_key(metadata.header.transaction_id))?;
    let retry = storage
        .get(snapshot, &retry_key)?
        .ok_or(DocumentError::Corrupt("retained D3 retry missing"))?;
    let retry_event = decode_retry_value(retry.bytes())?;
    if retry_event.database != view.database()
        || retry_event.shard != view.shard()
        || retry_event.origin != view.local_origin()
        || retry_event.sequence != metadata.header.id.sequence()
    {
        return Err(DocumentError::Corrupt("retained D3 retry changed"));
    }
    check_retained_rows(ctx, &frame)?;
    Ok(())
}

fn check_retained_rows(
    ctx: &AuditContext<'_>,
    frame: &tb_format::WalFrame<'_>,
) -> Result<(), DocumentError> {
    let AuditContext {
        storage,
        config,
        budget,
        view,
        limits,
        snapshot,
    } = *ctx;
    for operation in frame.operations() {
        let WalOperation::Put { key, value } = operation? else {
            unreachable!("verified source Put frame")
        };
        match decode_document_key(key)? {
            DocumentKey::Change { .. } => {
                let stored = storage
                    .get(snapshot, &Key::new(key.to_vec())?)?
                    .ok_or(DocumentError::Corrupt("retained D1 source missing"))?;
                if stored.bytes() != value {
                    return Err(DocumentError::Corrupt("retained D1 source changed"));
                }
            }
            DocumentKey::Primary { .. } => check_manifest_chain(
                storage,
                budget,
                config,
                view,
                limits,
                snapshot,
                (key, value),
            )?,
            _ => {}
        }
    }
    Ok(())
}

fn audit_visible_row(
    ctx: &AuditContext<'_>,
    row: &ScanRow,
    audit: &DistributedAudit,
) -> Result<(), DocumentError> {
    let AuditContext {
        storage,
        config,
        budget,
        view,
        limits,
        snapshot,
    } = *ctx;
    match row.key.as_bytes().first() {
        Some(0xC0) => {
            let DocumentKey::Catalog {
                collection,
                generation,
            } = decode_document_key(row.key.as_bytes())?
            else {
                return Err(DocumentError::Corrupt("invalid visible C0 key"));
            };
            if signed_catalog(view, collection, generation)? != row.value.as_slice() {
                return Err(DocumentError::Corrupt("altered visible C0"));
            }
        }
        Some(0xD0) => check_manifest_chain(
            storage,
            budget,
            config,
            view,
            limits,
            snapshot,
            (row.key.as_bytes(), &row.value),
        )?,
        Some(0xD1) => audit_visible_change(view, row, audit)?,
        Some(0xD2) => {
            let metadata = decode_metadata(&row.value, limits)?;
            if decode_document_key(row.key.as_bytes())?
                != (DocumentKey::Marker {
                    origin: metadata.header.id.origin(),
                    sequence: metadata.header.id.sequence(),
                })
                || metadata.header.id.database() != view.database()
                || metadata.header.id.shard() != view.shard()
                || metadata.header.id.origin() != view.local_origin()
                || metadata.header.id.sequence() > audit.local_sequence
                || metadata.header.record_format != 2
            {
                return Err(DocumentError::Corrupt("foreign visible D2"));
            }
        }
        Some(0xD3) => audit_visible_retry(ctx, row, audit.local_sequence)?,
        Some(0xD4) => {
            if decode_document_key(row.key.as_bytes())?
                != (DocumentKey::HighWater {
                    origin: view.local_origin(),
                })
                || decode_high_water_value(&row.value)? != audit.local_sequence
            {
                return Err(DocumentError::Corrupt("distributed D4 high-water mismatch"));
            }
        }
        Some(0xD5) => {
            let identity = decode_applied_identity(
                row.key.as_bytes(),
                &row.value,
                view.database(),
                view.shard(),
            )?;
            if identity.epoch != view.epoch()
                || identity.configuration != view.configuration().to_bytes()
                || identity.event.sequence
                    > audit
                        .applied_prefix
                        .get(&identity.event.origin)
                        .copied()
                        .unwrap_or(0)
            {
                return Err(DocumentError::Corrupt("foreign visible D5"));
            }
        }
        Some(0xD6) => check_pure_manifest(view, limits, row.key.as_bytes(), &row.value)?,
        _ => return Err(DocumentError::Corrupt("foreign visible distributed row")),
    }
    Ok(())
}

fn audit_visible_change(
    view: &CertifiedShardView,
    row: &ScanRow,
    audit: &DistributedAudit,
) -> Result<(), DocumentError> {
    let change = decode_change(&row.value)?;
    let DocumentKey::Change {
        collection,
        document,
        origin,
        sequence: change_sequence,
        ..
    } = decode_document_key(row.key.as_bytes())?
    else {
        return Err(DocumentError::Corrupt("invalid visible D1 key"));
    };
    if collection != change.binding.collection
        || document != change.binding.document
        || origin != change.event.origin
        || change_sequence != change.event.sequence
        || change.event.database != view.database()
        || change.event.shard != view.shard()
        || (change.event.origin == view.local_origin()
            && change.event.sequence > audit.local_sequence)
        || (change.event.origin != view.local_origin()
            && change.event.sequence
                > audit
                    .applied_prefix
                    .get(&change.event.origin)
                    .copied()
                    .unwrap_or(0))
    {
        return Err(DocumentError::Corrupt("foreign visible D1"));
    }
    check_signed_binding(
        view,
        CollectionBinding {
            collection: change.binding.collection,
            generation: change.binding.generation,
            schema_digest: change.binding.schema_digest,
        },
    )?;
    Ok(())
}

fn audit_visible_retry(
    ctx: &AuditContext<'_>,
    row: &ScanRow,
    sequence: u64,
) -> Result<(), DocumentError> {
    let AuditContext {
        storage,
        view,
        limits,
        snapshot,
        ..
    } = *ctx;
    let event = decode_retry_value(&row.value)?;
    let DocumentKey::Retry { transaction_id } = decode_document_key(row.key.as_bytes())? else {
        return Err(DocumentError::Corrupt("invalid visible D3 key"));
    };
    let marker = storage
        .get(
            snapshot,
            &key(encode_marker_key(event.origin, event.sequence)?)?,
        )?
        .ok_or(DocumentError::Corrupt("visible D3 marker missing"))?;
    let metadata = decode_metadata(marker.bytes(), limits)?;
    if event.database != view.database()
        || event.shard != view.shard()
        || event.origin != view.local_origin()
        || event.sequence > sequence
        || transaction_id != metadata.header.transaction_id
    {
        return Err(DocumentError::Corrupt("foreign visible D3"));
    }
    Ok(())
}

/// Check every retained group and visible row at one storage snapshot.
pub(crate) fn audit_distributed(
    storage: &Storage,
    config: &StorageConfig,
    budget: &MemoryBudget,
    view: &CertifiedShardView,
    limits: ReplicationLimits,
    snapshot: ReadSnapshot,
    require_complete_catalog: bool,
) -> Result<DistributedAudit, DocumentError> {
    let limits = effective_limits(limits);
    let ctx = AuditContext {
        storage,
        config,
        budget,
        view,
        limits,
        snapshot,
    };
    let audit = audit_retained_frames(&ctx)?;
    scan_all_visible(storage, config, budget, snapshot, |row| {
        audit_visible_row(&ctx, row, &audit)
    })?;
    if require_complete_catalog {
        for (binding, _) in view.catalogs() {
            let catalog_key = key(encode_catalog_key(binding.collection, binding.generation)?)?;
            if storage.get(snapshot, &catalog_key)?.is_none() {
                return Err(DocumentError::Corrupt("signed C0 row missing"));
            }
        }
    }
    let high_water_key = key(encode_high_water_key(view.local_origin())?)?;
    match storage.get(snapshot, &high_water_key)? {
        Some(value)
            if audit.local_sequence > 0
                && decode_high_water_value(value.bytes())? == audit.local_sequence => {}
        None if audit.local_sequence == 0 => {}
        _ => {
            return Err(DocumentError::Corrupt(
                "distributed high-water missing or changed",
            ));
        }
    }
    Ok(audit)
}

#[cfg(test)]
mod tests {
    use tb_replication::ReplicationLimits;

    use super::effective_limits;

    #[test]
    fn producer_causal_limits_never_exceed_receiver_contract() {
        let limits = ReplicationLimits {
            max_origins: 1_024,
            max_siblings: 1_024,
            max_changes: 510,
            max_value_bytes: tb_core::MAX_VALUE_BYTES,
            max_payload_bytes: tb_core::MAX_WAL_GROUP_BYTES,
        };
        let effective = effective_limits(limits);
        assert_eq!(effective.max_origins, 64);
        assert_eq!(effective.max_siblings, 64);
    }
}
