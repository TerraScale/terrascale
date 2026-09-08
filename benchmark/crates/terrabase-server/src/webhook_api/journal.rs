//! Retained document events and exact checkpoints for webhook delivery.

use super::{
    ChangeCheckpoint, DeliveryRecord, MAX_JOURNAL_PAGE, MetadataValue, Publication, StorageError,
    TenantScope, WebhookEngine, WebhookError, WebhookJournal, unsigned_from_value, unsigned_value,
    webhook_engine_scope,
};
use std::collections::BTreeMap;
use std::sync::Arc;
use terrabase::engine::Engine;
use terrabase::streaming::{SyncJournalMutation, SyncJournalRecord};
use terrabase::types::{Cancellation, DocumentValue};

/// One bounded retained-journal page, including skipped journal positions.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct PublicationPage {
    /// Document events in commit and write order.
    pub publications: Vec<Publication>,
    /// Exclusive position after all examined writes, even an empty page.
    pub next: Option<ChangeCheckpoint>,
}

/// Reads immutable retained commits, preserving overwritten revisions and deletes.
pub struct EngineWebhookJournal {
    engine: WebhookEngine,
}

impl EngineWebhookJournal {
    /// Binds the journal to an opened durable engine.
    #[must_use]
    pub fn new(engine: Arc<Engine>) -> Self {
        Self { engine: WebhookEngine::Engine(engine) }
    }

    /// Uses the database host admitted by the public API.
    #[must_use]
    pub fn from_host(host: Arc<terrabase_tbep::member_host::MemberHost>) -> Self {
        Self { engine: WebhookEngine::Host(host) }
    }
}

impl WebhookJournal for EngineWebhookJournal {
    fn page(
        &self,
        scope: &TenantScope,
        cursor: Option<ChangeCheckpoint>,
        limit: usize,
    ) -> Result<PublicationPage, StorageError> {
        let engine_scope = webhook_engine_scope(&self.engine, scope)?;
        if limit == 0 {
            return Ok(PublicationPage { publications: Vec::new(), next: cursor });
        }
        let limit =
            u32::try_from(limit.min(MAX_JOURNAL_PAGE)).map_err(|_| StorageError::Unavailable)?;
        let batch = self
            .engine
            .streams()
            .replay_sync_journal(&engine_scope, cursor, limit, &Cancellation::new())
            .map_err(|_| StorageError::Unavailable)?;
        let publications =
            batch.records().iter().map(publication).collect::<Result<Vec<_>, _>>()?;
        Ok(PublicationPage { publications, next: batch.next_checkpoint().or(cursor) })
    }
}

fn publication(record: &SyncJournalRecord) -> Result<Publication, StorageError> {
    let position = record.position();
    if position.sequence() == 0 || position.write_index() == 0 {
        return Err(StorageError::Unavailable);
    }
    let body = encode_publication(record)?;
    let origin_lane = record.provenance().origin().to_owned();
    let origin_region = terrabase::OriginLaneId::parse(&origin_lane)
        .map(|lane| lane.region().as_str().to_ascii_uppercase())
        .unwrap_or_else(|_| origin_lane.to_ascii_uppercase());
    Ok(Publication {
        revision: position.sequence(),
        write_index: position.write_index(),
        event_id: blake3::hash(&body).to_hex().to_string(),
        origin_region,
        origin_lane,
        body,
    })
}

// Versioned MessagePack event envelope. Ordered keys and native scalar encoding
// preserve integers, float values, and exact mutation bodies across retries.
fn encode_publication(record: &SyncJournalRecord) -> Result<Vec<u8>, StorageError> {
    use rmp::encode::{write_map_len, write_str, write_uint};
    let mut bytes = Vec::new();
    let encode = |_: rmp::encode::ValueWriteError| StorageError::Unavailable;
    write_map_len(&mut bytes, 8).map_err(encode)?;
    write_str(&mut bytes, "version").map_err(encode)?;
    write_uint(&mut bytes, 1).map_err(encode)?;
    write_str(&mut bytes, "sequence").map_err(encode)?;
    write_uint(&mut bytes, record.position().sequence()).map_err(encode)?;
    write_str(&mut bytes, "write_index").map_err(encode)?;
    write_uint(&mut bytes, u64::from(record.position().write_index())).map_err(encode)?;
    write_str(&mut bytes, "operation_id").map_err(encode)?;
    write_str(&mut bytes, record.operation_id().as_str()).map_err(encode)?;
    write_str(&mut bytes, "collection").map_err(encode)?;
    write_str(&mut bytes, record.collection().as_str()).map_err(encode)?;
    write_str(&mut bytes, "document_id").map_err(encode)?;
    write_str(&mut bytes, record.document().as_str()).map_err(encode)?;
    write_str(&mut bytes, "mutation").map_err(encode)?;
    let (mutation, value) = match record.mutation() {
        SyncJournalMutation::Put(value) => ("put", value),
        SyncJournalMutation::Delete => ("delete", &DocumentValue::Null),
    };
    write_str(&mut bytes, mutation).map_err(encode)?;
    write_str(&mut bytes, "value").map_err(encode)?;
    encode_value(&mut bytes, value)?;
    Ok(bytes)
}

fn encode_value(bytes: &mut Vec<u8>, value: &DocumentValue) -> Result<(), StorageError> {
    use rmp::encode;
    match value {
        DocumentValue::Null => encode::write_nil(bytes).map_err(|_| StorageError::Unavailable)?,
        DocumentValue::Boolean(value) => {
            encode::write_bool(bytes, *value).map_err(|_| StorageError::Unavailable)?;
        }
        DocumentValue::Integer(value) => {
            encode::write_sint(bytes, *value).map_err(|_| StorageError::Unavailable)?;
        }
        DocumentValue::Number(value) => {
            encode::write_f64(bytes, *value).map_err(|_| StorageError::Unavailable)?;
        }
        DocumentValue::String(value) => {
            encode::write_str(bytes, value).map_err(|_| StorageError::Unavailable)?;
        }
        DocumentValue::Array(values) => {
            encode::write_array_len(
                bytes,
                u32::try_from(values.len()).map_err(|_| StorageError::Unavailable)?,
            )
            .map_err(|_| StorageError::Unavailable)?;
            for value in values {
                encode_value(bytes, value)?;
            }
        }
        DocumentValue::Object(values) => {
            encode::write_map_len(
                bytes,
                u32::try_from(values.len()).map_err(|_| StorageError::Unavailable)?,
            )
            .map_err(|_| StorageError::Unavailable)?;
            for (key, value) in values {
                encode::write_str(bytes, key).map_err(|_| StorageError::Unavailable)?;
                encode_value(bytes, value)?;
            }
        }
    }
    Ok(())
}

pub(super) fn checkpoint_value(checkpoint: ChangeCheckpoint) -> MetadataValue {
    MetadataValue::Object(BTreeMap::from([
        ("sequence".into(), unsigned_value(checkpoint.sequence())),
        ("write_index".into(), unsigned_value(u64::from(checkpoint.write_index()))),
    ]))
}

pub(super) fn checkpoint_from_value(value: &MetadataValue) -> Option<ChangeCheckpoint> {
    let MetadataValue::Object(fields) = value else { return None };
    let sequence = unsigned_from_value(fields.get("sequence")?)?;
    let write_index = u32::try_from(unsigned_from_value(fields.get("write_index")?)?).ok()?;
    Some(ChangeCheckpoint::after(sequence, write_index))
}

pub(super) fn checkpoint_order(checkpoint: Option<ChangeCheckpoint>) -> Option<(u64, u32)> {
    checkpoint.map(|position| {
        (
            position.sequence(),
            if position.write_index() == 0 { u32::MAX } else { position.write_index() },
        )
    })
}

pub(super) fn before_publication(
    row: &DeliveryRecord,
) -> Result<Option<ChangeCheckpoint>, WebhookError> {
    let write_index = row
        .write_index
        .checked_sub(1)
        .ok_or_else(|| WebhookError::storage(StorageError::Unavailable))?;
    let position = if write_index == 0 {
        ChangeCheckpoint::after(
            row.revision
                .checked_sub(1)
                .ok_or_else(|| WebhookError::storage(StorageError::Unavailable))?,
            0,
        )
    } else {
        ChangeCheckpoint::after(row.revision, write_index)
    };
    Ok(Some(position))
}
