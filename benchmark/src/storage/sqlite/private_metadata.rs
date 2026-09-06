//! MVCC private control state committed with customer mutations.

use crate::{
    storage::{PrivateMetadataWrite, codec},
    types::{DocumentValue, TenantId},
};
use rusqlite::{Connection, OptionalExtension, params};

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
