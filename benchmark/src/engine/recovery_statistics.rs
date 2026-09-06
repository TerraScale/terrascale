//! Bounded logical recovery evidence independent of assigned document revisions.

use super::{Engine, EngineError};
use crate::types::{Cancellation, Document, DocumentValue, TenantId, TenantScope};

/// Logical document evidence at one fixed publication and TTL boundary.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct RecoveryStatistics {
    /// Publication boundary used for the scan.
    pub journal_cut: u64,
    /// Fixed TTL visibility clock in Unix milliseconds.
    pub captured_at_millis: u64,
    /// Number of live logical document winners.
    pub row_count: u64,
    /// BLAKE3 logical-content digest, excluding assigned document versions.
    ///
    /// The aggregate starts with `terrabase.logical-recovery.v1\0`, followed
    /// by 32-byte document hashes in tenant/collection/identifier lexical order.
    /// Each document hash starts with `terrabase.logical-document.v1\0`, then
    /// framed tenant, collection and identifier followed by its typed value.
    /// Framing means little-endian u64 byte length followed by UTF-8 bytes.
    /// Value tags are null=0, boolean=1, integer=2, float=3, string=4, array=5,
    /// object=6. Booleans append 0/1; integers append little-endian i64;
    /// floats append IEEE-754 bits as little-endian u64. Strings use framing.
    /// Arrays and objects append little-endian u64 entry counts, then values;
    /// object entries have framed keys in lexical order followed by values.
    pub checksum: [u8; 32],
}

impl Engine {
    /// Computes recovery evidence using pages capped at 256 documents and
    /// four MiB of stored bodies. This read emits no metering write facts.
    /// Source and restored target use the same TTL clock; their publication
    /// cuts may differ because restored documents receive fresh revisions.
    ///
    /// # Errors
    /// Rejects foreign scopes, unavailable snapshots, exceeded page bounds,
    /// cancellation, and storage failures. No partial digest is returned.
    pub fn recovery_statistics(
        &self,
        scope: &TenantScope,
        boundary: u64,
        captured_at_millis: u64,
        cancellation: Option<&Cancellation>,
    ) -> Result<RecoveryStatistics, EngineError> {
        let mut hasher = logical_state_hasher();
        let mut cursor = None;
        let mut row_count = 0_u64;
        loop {
            check_cancelled(cancellation)?;
            let records = self.export_snapshot_records(
                scope,
                boundary,
                captured_at_millis,
                cursor.as_ref(),
                256,
            )?;
            if records.is_empty() {
                break;
            }
            for record in &records {
                check_cancelled(cancellation)?;
                hasher.update(&logical_document_digest(scope.tenant(), record.document()));
                row_count = row_count.checked_add(1).ok_or(EngineError::StorageFailure)?;
            }
            cursor = records.last().map(|record| {
                (record.document().collection().clone(), record.document().id().clone())
            });
        }
        check_cancelled(cancellation)?;
        Ok(RecoveryStatistics {
            journal_cut: boundary,
            captured_at_millis,
            row_count,
            checksum: *hasher.finalize().as_bytes(),
        })
    }
}

fn check_cancelled(cancellation: Option<&Cancellation>) -> Result<(), EngineError> {
    if cancellation.is_some_and(Cancellation::is_cancelled) {
        return Err(EngineError::Cancelled);
    }
    Ok(())
}

pub(crate) fn logical_state_hasher() -> blake3::Hasher {
    let mut hasher = blake3::Hasher::new();
    hasher.update(b"terrabase.logical-recovery.v1\0");
    hasher
}

/// Hashes one tenant/key/value without revision metadata for recovery evidence.
pub(crate) fn logical_document_digest(tenant: &TenantId, document: &Document) -> [u8; 32] {
    let mut hasher = blake3::Hasher::new();
    hasher.update(b"terrabase.logical-document.v1\0");
    hash_bytes(&mut hasher, tenant.as_str().as_bytes());
    hash_document(&mut hasher, document);
    *hasher.finalize().as_bytes()
}

fn hash_document(hasher: &mut blake3::Hasher, document: &Document) {
    hash_bytes(hasher, document.collection().as_str().as_bytes());
    hash_bytes(hasher, document.id().as_str().as_bytes());
    hash_value(hasher, document.value());
}

fn hash_bytes(hasher: &mut blake3::Hasher, bytes: &[u8]) {
    hasher.update(&(bytes.len() as u64).to_le_bytes());
    hasher.update(bytes);
}

fn hash_value(hasher: &mut blake3::Hasher, value: &DocumentValue) {
    match value {
        DocumentValue::Null => {
            hasher.update(&[0]);
        }
        DocumentValue::Boolean(value) => {
            hasher.update(&[1, u8::from(*value)]);
        }
        DocumentValue::Integer(value) => {
            hasher.update(&[2]);
            hasher.update(&value.to_le_bytes());
        }
        DocumentValue::Number(value) => {
            hasher.update(&[3]);
            hasher.update(&value.to_bits().to_le_bytes());
        }
        DocumentValue::String(value) => {
            hasher.update(&[4]);
            hash_bytes(hasher, value.as_bytes());
        }
        DocumentValue::Array(values) => {
            hasher.update(&[5]);
            hasher.update(&(values.len() as u64).to_le_bytes());
            for value in values {
                hash_value(hasher, value);
            }
        }
        DocumentValue::Object(values) => {
            hasher.update(&[6]);
            hasher.update(&(values.len() as u64).to_le_bytes());
            for (key, value) in values {
                hash_bytes(hasher, key.as_bytes());
                hash_value(hasher, value);
            }
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::types::{CollectionName, DocumentId};

    fn digest(document: &Document) -> [u8; 32] {
        logical_document_digest(&TenantId::try_new("tenant").unwrap(), document)
    }

    #[test]
    fn logical_checksum_ignores_versions_but_detects_content_and_key_changes() {
        let mut source = Document::new(
            DocumentId::try_new("id").unwrap(),
            CollectionName::try_new("items").unwrap(),
            DocumentValue::Array(vec![
                DocumentValue::Integer(1),
                DocumentValue::String("value".into()),
            ]),
        );
        source.set_version(17);
        let mut restored = source.clone();
        restored.set_version(1);
        assert_eq!(digest(&source), digest(&restored));
        let changed = Document::new(
            source.id().clone(),
            source.collection().clone(),
            DocumentValue::Integer(1),
        );
        assert_ne!(digest(&source), digest(&changed));
        let renamed = Document::new(
            DocumentId::try_new("other").unwrap(),
            source.collection().clone(),
            source.value().clone(),
        );
        assert_ne!(digest(&source), digest(&renamed));
        let float = Document::new(
            source.id().clone(),
            source.collection().clone(),
            DocumentValue::Number(1.0),
        );
        assert_ne!(digest(&changed), digest(&float));
    }
}
