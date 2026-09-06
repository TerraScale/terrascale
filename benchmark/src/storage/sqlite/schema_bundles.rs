//! Collection-wide schema conflict arbitration with immutable generation ownership.

use super::private_metadata;
use crate::{
    storage::{CommitIntent, JournalProvenance, PrivateMetadataWrite, SchemaEnvelopeWrite},
    types::{DocumentValue, TenantId},
};
use rusqlite::{Connection, params};
use std::collections::{BTreeMap, BTreeSet};

const PREFIX: &str = "schema:bundle/";

pub(super) fn collection(key: &str) -> Option<&str> {
    key.strip_prefix(PREFIX)
        .or_else(|| key.strip_prefix("family:validation/"))
        .or_else(|| key.strip_prefix("family:migration/"))
}

fn identity(value: &DocumentValue) -> Result<(i64, String), ()> {
    let DocumentValue::Object(fields) = value else { return Err(()) };
    let Some(DocumentValue::Integer(generation)) = fields.get("generation") else { return Err(()) };
    let Some(DocumentValue::String(manifest)) = fields.get("manifest") else { return Err(()) };
    if *generation <= 0 || manifest.len() != 64 || !manifest.bytes().all(|b| b.is_ascii_hexdigit())
    {
        return Err(());
    }
    Ok((*generation, manifest.clone()))
}

fn stamp(value: &DocumentValue) -> Result<(i64, u64, String, String), ()> {
    let (generation, manifest) = identity(value)?;
    let DocumentValue::Object(fields) = value else { return Err(()) };
    let Some(DocumentValue::String(hlc)) = fields.get("initial_hlc") else { return Err(()) };
    let Some(DocumentValue::String(origin)) = fields.get("initial_origin") else { return Err(()) };
    Ok((generation, hlc.parse().map_err(|_| ())?, origin.clone(), manifest))
}

pub(super) fn collections(intent: &CommitIntent) -> BTreeSet<String> {
    intent
        .metadata
        .iter()
        .filter_map(|write| collection(&write.key).map(str::to_owned))
        .chain(intent.schema_envelopes.iter().map(|write| write.collection.clone()))
        .collect()
}

/// Normalizes local markers and chooses entire incoming bundles before any row changes.
pub(super) fn prepare(
    connection: &Connection,
    intent: &CommitIntent,
    provenance: &JournalProvenance,
) -> Result<(Vec<PrivateMetadataWrite>, BTreeSet<String>), ()> {
    let mut writes = intent.metadata.clone();
    let mut rejected = BTreeSet::new();
    for collection in collections(intent) {
        let key = format!("{PREFIX}{collection}");
        let current = private_metadata::read_private_metadata_on(
            connection,
            &intent.tenant,
            &key,
            i64::MAX as u64,
        )?;
        let incoming =
            writes.iter().find(|write| write.key == key).and_then(|write| write.value.clone());
        if intent.replication_publication.is_some() {
            match (incoming.as_ref(), current.as_ref()) {
                (Some(incoming), Some(current)) => {
                    let incoming_stamp = stamp(incoming)?;
                    let current_stamp = stamp(current)?;
                    if incoming_stamp < current_stamp
                        || (incoming_stamp == current_stamp
                            && !private_metadata::incoming_control_wins(
                                connection,
                                &intent.tenant,
                                &format!("metadata:{key}"),
                                provenance,
                            )?)
                    {
                        rejected.insert(collection);
                    }
                }
                (None, Some(_)) => {
                    rejected.insert(collection);
                }
                (Some(incoming), None) => {
                    stamp(incoming)?;
                }
                (None, None) => {}
            }
            continue;
        }
        let Some(mut marker) = incoming.or_else(|| current.clone()) else { continue };
        let unchanged = current
            .as_ref()
            .is_some_and(|current| identity(current).ok() == identity(&marker).ok());
        if unchanged {
            marker = current.ok_or(())?;
        } else {
            identity(&marker)?;
            let DocumentValue::Object(fields) = &mut marker else { return Err(()) };
            fields
                .insert("initial_hlc".into(), DocumentValue::String(provenance.hlc().to_string()));
            fields.insert(
                "initial_origin".into(),
                DocumentValue::String(provenance.origin().to_owned()),
            );
        }
        if let Some(write) = writes.iter_mut().find(|write| write.key == key) {
            write.value = Some(marker);
        } else {
            writes.push(PrivateMetadataWrite { key, value: Some(marker) });
        }
    }
    Ok((writes, rejected))
}

/// Includes all controls of every touched bundle in the original source payload.
pub(super) fn source_snapshot(
    connection: &Connection,
    intent: &CommitIntent,
) -> Result<(Vec<PrivateMetadataWrite>, Vec<SchemaEnvelopeWrite>), ()> {
    let mut metadata: BTreeMap<String, PrivateMetadataWrite> =
        intent.metadata.iter().cloned().map(|write| (write.key.clone(), write)).collect();
    let mut envelopes: BTreeMap<(String, String, String), SchemaEnvelopeWrite> = intent
        .schema_envelopes
        .iter()
        .cloned()
        .map(|write| {
            ((write.database.clone(), write.branch.clone(), write.collection.clone()), write)
        })
        .collect();
    for collection in collections(intent) {
        let marker = format!("{PREFIX}{collection}");
        if private_metadata::read_private_metadata_on(
            connection,
            &intent.tenant,
            &marker,
            i64::MAX as u64,
        )?
        .is_none()
        {
            continue;
        }
        for key in [
            marker,
            format!("family:validation/{collection}"),
            format!("family:migration/{collection}"),
        ] {
            let value = private_metadata::read_private_metadata_on(
                connection,
                &intent.tenant,
                &key,
                i64::MAX as u64,
            )?;
            metadata.insert(key.clone(), PrivateMetadataWrite { key, value });
        }
        let mut statement=connection.prepare("SELECT database_id,branch,envelope FROM schema_metadata WHERE collection=?1 ORDER BY database_id,branch LIMIT 1025").map_err(|_|())?;
        let mut rows = statement.query([&collection]).map_err(|_| ())?;
        let mut count = 0;
        while let Some(row) = rows.next().map_err(|_| ())? {
            count += 1;
            if count > 1024 {
                return Err(());
            }
            let database: String = row.get(0).map_err(|_| ())?;
            let branch: String = row.get(1).map_err(|_| ())?;
            let replacement: Vec<u8> = row.get(2).map_err(|_| ())?;
            envelopes.insert(
                (database.clone(), branch.clone(), collection.clone()),
                SchemaEnvelopeWrite {
                    database,
                    branch,
                    collection: collection.clone(),
                    expected: None,
                    replacement,
                },
            );
        }
    }
    Ok((metadata.into_values().collect(), envelopes.into_values().collect()))
}

/// Progress begun against another schema generation must retry before writing anything.
pub(super) fn changed_since_snapshot(
    connection: &Connection,
    tenant: &TenantId,
    intent: &CommitIntent,
) -> Result<bool, ()> {
    if intent.replication_publication.is_some() {
        return Ok(false);
    }
    for collection in collections(intent) {
        let key = format!("{PREFIX}{collection}");
        let latest: Option<i64> = connection
            .query_row(
                "SELECT MAX(seq) FROM family_metadata WHERE tenant=?1 AND record_key=?2",
                params![tenant.as_str(), key],
                |row| row.get(0),
            )
            .map_err(|_| ())?;
        if latest.is_some_and(|sequence| sequence > intent.snapshot_sequence.get() as i64) {
            return Ok(true);
        }
    }
    Ok(false)
}
