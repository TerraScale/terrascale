//! Original source transaction groups retained independently of merged document rows.

use crate::{
    engine::{ReplicatedPublication, ReplicatedRecord},
    storage::{CommitIntent, JournalProvenance, StagedWrite, codec},
    types::{CollectionName, DocumentId, DocumentValue, OperationId, TenantId},
};
use rusqlite::{Connection, params};

pub(super) fn persist_publication(
    connection: &Connection,
    intent: &CommitIntent,
    provenance: &JournalProvenance,
    operation: &OperationId,
    local_sequence: u64,
) -> Result<(), ()> {
    // The retired per-record apply seam has no complete source transaction.
    // Never advertise those fragments as a retained atomic publication.
    if provenance.kind() == crate::storage::JournalKind::Replicated
        && intent.replication_publication.is_none()
    {
        return Ok(());
    }
    let payload = if let Some(group) = &intent.replication_publication {
        group.private_payload.clone()
    } else {
        let (metadata, envelopes) = super::schema_bundles::source_snapshot(connection, intent)?;
        codec::encode_private_changes(&metadata, &envelopes)
    };
    let now = i64::try_from(
        std::time::SystemTime::now()
            .duration_since(std::time::UNIX_EPOCH)
            .map_err(|_| ())?
            .as_millis(),
    )
    .map_err(|_| ())?;
    let previous: i64 = connection
        .query_row(
            "SELECT MAX(value) FROM (SELECT COALESCE(MAX(committed_at),0) AS value FROM replication_publications UNION ALL SELECT CAST(value AS INTEGER) FROM meta WHERE key='commit_timestamp_floor')",
            [],
            |row| row.get(0),
        )
        .map_err(|_| ())?;
    let committed_at = now.max(previous);
    connection.execute("INSERT INTO meta (key,value) VALUES ('commit_timestamp_floor',?1) ON CONFLICT(key) DO UPDATE SET value=excluded.value",[committed_at]).map_err(|_|())?;
    connection.execute(
        "INSERT INTO replication_publications (tenant, origin, origin_seq, hlc, operation, local_seq, committed_at, context, private_payload) VALUES (?1, ?2, ?3, ?4, ?5, ?6, ?7, ?8, ?9)",
        params![intent.tenant.as_str(), provenance.origin(), provenance.origin_sequence() as i64, provenance.hlc() as i64, operation.as_str(), local_sequence as i64, committed_at, provenance.context(), payload],
    ).map_err(|_| ())?;
    let records = if let Some(group) = &intent.replication_publication {
        group.records.clone()
    } else {
        intent
            .writes
            .iter()
            .filter_map(|write| {
                let (collection, id, body, deleted) = match write {
                    StagedWrite::Put { document, .. }
                    | StagedWrite::ReplicatedPut { document, .. } => (
                        document.collection().clone(),
                        document.id().clone(),
                        document.value().clone(),
                        false,
                    ),
                    StagedWrite::Delete { collection: Some(collection), id, .. }
                    | StagedWrite::ReplicatedDelete { collection, id, .. } => {
                        (collection.clone(), id.clone(), DocumentValue::Null, true)
                    }
                    StagedWrite::Delete { collection: None, .. } => return None,
                };
                Some(ReplicatedRecord {
                    collection,
                    id,
                    body,
                    deleted,
                    commit_ulid: operation.as_str().to_owned(),
                    hlc: provenance.hlc(),
                    origin: provenance.origin().to_owned(),
                    origin_sequence: provenance.origin_sequence(),
                    context: provenance.context().to_vec(),
                })
            })
            .collect()
    };
    for (index, record) in records.iter().enumerate() {
        let body = if record.deleted {
            None
        } else {
            Some(codec::encode_private_value(&record.body).ok_or(())?)
        };
        connection.execute("INSERT INTO replication_publication_records (tenant, local_seq, ordinal, collection, id, body) VALUES (?1, ?2, ?3, ?4, ?5, ?6)", params![intent.tenant.as_str(), local_sequence as i64, index as i64, record.collection.as_str(), record.id.as_str(), body]).map_err(|_| ())?;
    }
    super::repair_summary::persist(
        connection,
        &intent.tenant,
        local_sequence,
        &ReplicatedPublication {
            origin: provenance.origin().to_owned(),
            origin_sequence: provenance.origin_sequence(),
            hlc: provenance.hlc(),
            commit_ulid: intent
                .replication_publication
                .as_ref()
                .map_or_else(|| operation.as_str().to_owned(), |group| group.commit_ulid.clone()),
            context: provenance.context().to_vec(),
            private_payload: payload,
            records,
        },
    )?;
    Ok(())
}

pub(super) fn read_publications(
    connection: &Connection,
    tenant: &TenantId,
    after: u64,
    limit: u32,
) -> Result<Vec<(u64, ReplicatedPublication)>, ()> {
    let mut statement = connection.prepare("SELECT origin, origin_seq, hlc, operation, local_seq, context, private_payload FROM replication_publications WHERE tenant = ?1 AND local_seq > ?2 ORDER BY local_seq LIMIT ?3").map_err(|_| ())?;
    let mut rows = statement
        .query(params![tenant.as_str(), i64::try_from(after).map_err(|_| ())?, limit])
        .map_err(|_| ())?;
    let mut result = Vec::new();
    let mut bytes = 0_usize;
    while let Some(row) = rows.next().map_err(|_| ())? {
        let private_payload: Vec<u8> = row.get(6).map_err(|_| ())?;
        let context: Vec<u8> = row.get(5).map_err(|_| ())?;
        bytes = bytes
            .checked_add(private_payload.len())
            .and_then(|bytes| bytes.checked_add(context.len()))
            .ok_or(())?;
        if bytes > 64 * 1024 * 1024 {
            return Err(());
        }
        let local_sequence =
            u64::try_from(row.get::<_, i64>(4).map_err(|_| ())?).map_err(|_| ())?;
        let mut publication = ReplicatedPublication {
            origin: row.get(0).map_err(|_| ())?,
            origin_sequence: u64::try_from(row.get::<_, i64>(1).map_err(|_| ())?)
                .map_err(|_| ())?,
            hlc: u64::from_le_bytes(row.get::<_, i64>(2).map_err(|_| ())?.to_le_bytes()),
            commit_ulid: row.get(3).map_err(|_| ())?,
            context,
            private_payload,
            records: Vec::new(),
        };
        let mut records = connection.prepare("SELECT collection, id, body FROM replication_publication_records WHERE tenant = ?1 AND local_seq = ?2 ORDER BY ordinal").map_err(|_| ())?;
        let mut records =
            records.query(params![tenant.as_str(), local_sequence as i64]).map_err(|_| ())?;
        while let Some(row) = records.next().map_err(|_| ())? {
            if publication.records.len() == 100_000 {
                return Err(());
            }
            let collection: String = row.get(0).map_err(|_| ())?;
            let id: String = row.get(1).map_err(|_| ())?;
            let encoded: Option<Vec<u8>> = row.get(2).map_err(|_| ())?;
            bytes = bytes
                .checked_add(encoded.as_ref().map_or(0, Vec::len))
                .and_then(|bytes| bytes.checked_add(collection.len() + id.len()))
                .ok_or(())?;
            if bytes > 64 * 1024 * 1024 {
                return Err(());
            }
            let body = encoded
                .as_ref()
                .map(|bytes| codec::decode_private_value(bytes).ok_or(()))
                .transpose()?;
            publication.records.push(ReplicatedRecord {
                collection: CollectionName::try_new(&collection).map_err(|_| ())?,
                id: DocumentId::try_new(&id).map_err(|_| ())?,
                deleted: body.is_none(),
                body: body.unwrap_or(DocumentValue::Null),
                origin: publication.origin.clone(),
                origin_sequence: publication.origin_sequence,
                hlc: publication.hlc,
                commit_ulid: publication.commit_ulid.clone(),
                context: publication.context.clone(),
            });
        }
        result.push((local_sequence, publication));
    }
    Ok(result)
}
