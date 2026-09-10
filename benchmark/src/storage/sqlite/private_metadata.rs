//! MVCC private control state committed with customer mutations.

use crate::{
    storage::{PrivateMetadataWrite, codec},
    types::{DocumentValue, TenantId},
};
use rusqlite::{Connection, OptionalExtension, params};

/// Checks a generated marker against Engine's independently admitted owner/lane.
pub(super) fn validate_primary_identity<'a>(
    intent: &'a crate::storage::CommitIntent,
    admitted: Option<&PrivateMetadataWrite>,
) -> Result<Option<&'a PrivateMetadataWrite>, crate::storage::CommitRejection> {
    use crate::storage::CommitRejection;
    let Some(_) =
        intent.validated_generated_identity().map_err(|()| CommitRejection::Validation)?
    else {
        return Ok(None);
    };
    let key = intent.generated_primary_file_identity.as_ref().ok_or(CommitRejection::Validation)?;
    let generated = intent
        .metadata
        .iter()
        .find(|write| &write.key == key)
        .ok_or(CommitRejection::Validation)?;
    let admitted = admitted.ok_or(CommitRejection::Conflict)?;
    if generated.key != admitted.key || generated.value != admitted.value {
        return Err(CommitRejection::Conflict);
    }
    Ok(Some(generated))
}

/// Resolves validated generated initialization under the same writer transaction
/// as customer OCC and publication. Unmarked metadata never takes this path.
pub(super) fn initialize_primary_identity<'a>(
    connection: &Connection,
    intent: &'a crate::storage::CommitIntent,
    generated: Option<&PrivateMetadataWrite>,
) -> Result<std::borrow::Cow<'a, crate::storage::CommitIntent>, crate::storage::CommitRejection> {
    use crate::storage::CommitRejection;
    let Some(generated) = generated else { return Ok(std::borrow::Cow::Borrowed(intent)) };
    let key = &generated.key;
    let current: Option<Option<Vec<u8>>> = connection.query_row(
        "SELECT value FROM family_metadata WHERE tenant=?1 AND record_key=?2 ORDER BY seq DESC LIMIT 1",
        params![intent.tenant.as_str(), key], |row| row.get(0),
    ).optional().map_err(|_| CommitRejection::Storage)?;
    let Some(current) = current else { return Ok(std::borrow::Cow::Borrowed(intent)) };
    let Some(current) = current else { return Err(CommitRejection::Conflict) };
    let current = codec::decode_private_value(&current).ok_or(CommitRejection::Storage)?;
    let expected = generated.value.as_ref().ok_or(CommitRejection::Validation)?;
    if &current != expected {
        return Err(CommitRejection::Conflict);
    }
    let mut initialized = intent.clone();
    initialized.metadata.retain(|write| &write.key != key);
    initialized.generated_primary_file_identity = None;
    Ok(std::borrow::Cow::Owned(initialized))
}

pub(super) fn read_private_metadata_on(
    connection: &Connection,
    tenant: &TenantId,
    key: &str,
    sequence: u64,
) -> Result<Option<DocumentValue>, ()> {
    let row: Option<Option<Vec<u8>>> = connection.query_row(
        "SELECT value FROM family_metadata WHERE tenant = ?1 AND record_key = ?2 AND seq <= ?3 ORDER BY seq DESC LIMIT 1",
        params![tenant.as_str(), key, i64::try_from(sequence).map_err(|_| ())?], |row| row.get(0)
    ).optional().map_err(|_| ())?;
    row.flatten().map(|bytes| codec::decode_private_value(&bytes).ok_or(())).transpose()
}

pub(super) fn valid_metadata(writes: &[PrivateMetadataWrite]) -> bool {
    let mut keys = std::collections::BTreeSet::new();
    writes.len() <= 1024
        && writes.iter().all(|write| {
            !write.key.is_empty()
                && write.key.len() <= 4096
                && keys.insert(&write.key)
                && write
                    .value
                    .as_ref()
                    .is_none_or(|value| codec::encode_private_value(value).is_some())
        })
}

pub(super) fn metadata_conflicts(
    connection: &Connection,
    tenant: &TenantId,
    writes: &[PrivateMetadataWrite],
    snapshot: u64,
) -> Result<bool, ()> {
    for write in writes {
        let latest: Option<i64> = connection
            .query_row(
                "SELECT MAX(seq) FROM family_metadata WHERE tenant = ?1 AND record_key = ?2",
                params![tenant.as_str(), write.key],
                |row| row.get(0),
            )
            .map_err(|_| ())?;
        if latest.is_some_and(|sequence| {
            u64::try_from(sequence).map_or(true, |sequence| sequence > snapshot)
        }) {
            return Ok(true);
        }
    }
    Ok(false)
}

pub(super) fn stage_metadata(
    connection: &Connection,
    tenant: &TenantId,
    writes: &[PrivateMetadataWrite],
    sequence: u64,
) -> Result<(), ()> {
    for write in writes {
        let encoded = write
            .value
            .as_ref()
            .map(|value| codec::encode_private_value(value).ok_or(()))
            .transpose()?;
        connection.execute("INSERT INTO family_metadata (tenant, record_key, seq, value) VALUES (?1, ?2, ?3, ?4)", params![tenant.as_str(), write.key, i64::try_from(sequence).map_err(|_| ())?, encoded]).map_err(|_| ())?;
    }
    Ok(())
}

pub(super) fn valid_envelopes(writes: &[crate::storage::SchemaEnvelopeWrite]) -> bool {
    let mut keys = std::collections::BTreeSet::new();
    writes.len() <= 1024
        && writes.iter().all(|write| {
            keys.insert((&write.database, &write.branch, &write.collection))
                && write.replacement.len() <= 16 * 1024 * 1024
                && write.expected.as_ref().is_none_or(|value| value.len() <= 16 * 1024 * 1024)
        })
}

pub(super) fn envelopes_conflict(
    connection: &Connection,
    writes: &[crate::storage::SchemaEnvelopeWrite],
) -> Result<bool, ()> {
    for write in writes {
        let current: Option<Vec<u8>> = connection.query_row(
            "SELECT envelope FROM schema_metadata WHERE database_id = ?1 AND branch = ?2 AND collection = ?3",
            params![write.database, write.branch, write.collection], |row| row.get(0)
        ).optional().map_err(|_| ())?;
        if current != write.expected {
            return Ok(true);
        }
    }
    Ok(false)
}

pub(super) fn stage_envelopes(
    connection: &Connection,
    writes: &[crate::storage::SchemaEnvelopeWrite],
) -> Result<(), ()> {
    for write in writes {
        connection.execute(
            "INSERT INTO schema_metadata (database_id, branch, collection, envelope) VALUES (?1, ?2, ?3, ?4) ON CONFLICT(database_id, branch, collection) DO UPDATE SET envelope = excluded.envelope",
            params![write.database, write.branch, write.collection, write.replacement],
        ).map_err(|_| ())?;
    }
    Ok(())
}

/// Applies each causally newer private control, then deterministic concurrent winner.
pub(super) fn stage_controls(
    connection: &Connection,
    intent: &crate::storage::CommitIntent,
    provenance: &crate::storage::JournalProvenance,
    sequence: u64,
) -> Result<(), ()> {
    let (metadata, rejected) = super::schema_bundles::prepare(connection, intent, provenance)?;
    for write in &metadata {
        if super::schema_bundles::collection(&write.key)
            .is_some_and(|collection| rejected.contains(collection))
        {
            continue;
        }
        let key = format!("metadata:{}", write.key);
        let bundled = super::schema_bundles::collection(&write.key).is_some_and(|collection| {
            metadata.iter().any(|candidate| candidate.key == format!("schema:bundle/{collection}"))
        });
        if intent.replication_publication.is_none()
            || bundled
            || incoming_control_wins(connection, &intent.tenant, &key, provenance)?
        {
            stage_metadata(connection, &intent.tenant, std::slice::from_ref(write), sequence)?;
            record_control_version(connection, &intent.tenant, &key, provenance)?;
        }
    }
    for write in &intent.schema_envelopes {
        if rejected.contains(&write.collection) {
            continue;
        }
        let key = format!(
            "schema:{}:{}:{}:{}:{}:{}",
            write.database.len(),
            write.database,
            write.branch.len(),
            write.branch,
            write.collection.len(),
            write.collection
        );
        let bundled = metadata
            .iter()
            .any(|candidate| candidate.key == format!("schema:bundle/{}", write.collection));
        if intent.replication_publication.is_none()
            || bundled
            || incoming_control_wins(connection, &intent.tenant, &key, provenance)?
        {
            stage_envelopes(connection, std::slice::from_ref(write))?;
            record_control_version(connection, &intent.tenant, &key, provenance)?;
        }
    }
    Ok(())
}

pub(super) fn incoming_control_wins(
    connection: &Connection,
    tenant: &TenantId,
    key: &str,
    incoming: &crate::storage::JournalProvenance,
) -> Result<bool, ()> {
    let current: Option<(String,i64,i64,Vec<u8>)> = connection.query_row(
        "SELECT origin, origin_seq, hlc, context FROM private_control_versions WHERE tenant = ?1 AND control_key = ?2",
        params![tenant.as_str(),key], |row| Ok((row.get(0)?,row.get(1)?,row.get(2)?,row.get(3)?)),
    ).optional().map_err(|_| ())?;
    let Some((origin, sequence, hlc, context)) = current else {
        return Ok(true);
    };
    let sequence = u64::try_from(sequence).map_err(|_| ())?;
    let incoming_context = crate::engine::merge::decode_context(incoming.context()).ok_or(())?;
    let current_context = crate::engine::merge::decode_context(&context).ok_or(())?;
    if incoming_context.contains(&crate::engine::merge::DocumentDot::new(origin.clone(), sequence))
    {
        return Ok(true);
    }
    if current_context.contains(&crate::engine::merge::DocumentDot::new(
        incoming.origin().to_owned(),
        incoming.origin_sequence(),
    )) {
        return Ok(false);
    }
    Ok((incoming.hlc(), incoming.origin())
        > (u64::from_le_bytes(hlc.to_le_bytes()), origin.as_str()))
}

fn record_control_version(
    connection: &Connection,
    tenant: &TenantId,
    key: &str,
    provenance: &crate::storage::JournalProvenance,
) -> Result<(), ()> {
    connection.execute(
        "INSERT INTO private_control_versions (tenant,control_key,origin,origin_seq,hlc,context) VALUES (?1,?2,?3,?4,?5,?6) ON CONFLICT(tenant,control_key) DO UPDATE SET origin=excluded.origin,origin_seq=excluded.origin_seq,hlc=excluded.hlc,context=excluded.context",
        params![tenant.as_str(),key,provenance.origin(),provenance.origin_sequence() as i64,provenance.hlc() as i64,provenance.context()],
    ).map_err(|_| ())?;
    Ok(())
}
