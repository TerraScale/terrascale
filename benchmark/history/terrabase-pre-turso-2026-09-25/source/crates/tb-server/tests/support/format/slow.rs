//! Persistent format round trip through public local-storage and format APIs.

use std::fs;
use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::{SystemTime, UNIX_EPOCH};

use tb_core::{
    BudgetConfig, CollectionId, DocumentId, Key, KeyRange, MemoryBudget, TenantId, TransactionId,
    WorkerId,
};
use tb_format::{
    DocumentBinding, DocumentFieldInput, FieldType, SchemaField, decode_catalog, decode_document,
    decode_schema, encode_catalog_into, encode_document_into, encode_schema_into,
};
use tb_storage::{Mutation, Storage, StorageConfig};

static NEXT_DIRECTORY: AtomicU64 = AtomicU64::new(0);

struct TempShard(PathBuf);

impl TempShard {
    fn new() -> Self {
        let nanos = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .expect("system clock is after the Unix epoch")
            .as_nanos();
        let ordinal = NEXT_DIRECTORY.fetch_add(1, Ordering::Relaxed);
        Self(std::env::temp_dir().join(format!(
            "terrabase-slow-format-{}-{nanos}-{ordinal}",
            std::process::id()
        )))
    }

    fn path(&self) -> &Path {
        &self.0
    }
}

impl Drop for TempShard {
    fn drop(&mut self) {
        if self.0.exists() {
            fs::remove_dir_all(&self.0).expect("remove temporary format shard");
        }
    }
}

fn budget() -> MemoryBudget {
    MemoryBudget::new(BudgetConfig::new(64 * 1024 * 1024, 0).expect("valid test budget"))
}

#[test]
fn encoded_catalog_and_document_survive_checkpoint_reopen_and_decode() {
    let directory = TempShard::new();
    let config = StorageConfig::local(
        directory.path(),
        TenantId::from_bytes([0x11; 16]),
        WorkerId(1),
    )
    .with_clock(Arc::new(|| 1_000));
    let collection = CollectionId::from_bytes([0x21; 16]);
    let document_id = DocumentId::from_bytes([0x31; 16]);

    let schema_fields = [SchemaField {
        field_id: 1,
        parent_field_id: 0,
        value_type: FieldType::Utf8,
        element_type: None,
        flags: 1,
        name: "label",
    }];
    let mut schema_bytes = Vec::new();
    encode_schema_into(&mut schema_bytes, &schema_fields).expect("encode schema");
    let mut catalog_bytes = Vec::new();
    let schema_digest = encode_catalog_into(&mut catalog_bytes, collection, 1, 0, &schema_bytes)
        .expect("encode catalog");
    let binding = DocumentBinding {
        collection,
        document: document_id,
        generation: 1,
        schema_digest,
    };
    let fields = [DocumentFieldInput {
        field_id: 1,
        value_type: FieldType::Utf8,
        value: b"survives reopen",
    }];
    let mut document_bytes = Vec::new();
    encode_document_into(&mut document_bytes, binding, &fields).expect("encode document");

    let catalog_key = Key::new(b"format/catalog".to_vec()).expect("catalog key");
    let document_key = Key::new(b"format/document".to_vec()).expect("document key");
    let storage = Storage::open(config.clone(), budget()).expect("open local storage");
    storage
        .commit_batch(
            storage.snapshot(),
            TransactionId::new(50_000, [0x41; 16]),
            &[
                Mutation::Put {
                    key: catalog_key.clone(),
                    value: catalog_bytes.clone(),
                },
                Mutation::Put {
                    key: document_key.clone(),
                    value: document_bytes.clone(),
                },
            ],
        )
        .expect("persist encoded format records");
    storage.checkpoint().expect("checkpoint format records");
    drop(storage);

    let reopened = Storage::open(config, budget()).expect("reopen local storage");
    let rows = reopened
        .scan(reopened.snapshot(), &KeyRange::all(), 2)
        .expect("read format records after reopen");
    assert_eq!(rows.rows().len(), 2);
    let recovered_catalog = rows
        .rows()
        .iter()
        .find(|row| row.key == catalog_key)
        .expect("recovered catalog row");
    let recovered_document = rows
        .rows()
        .iter()
        .find(|row| row.key == document_key)
        .expect("recovered document row");
    assert_eq!(recovered_catalog.value, catalog_bytes);
    assert_eq!(recovered_document.value, document_bytes);

    let catalog = decode_catalog(&recovered_catalog.value).expect("decode recovered catalog");
    assert_eq!(catalog.collection, collection);
    assert_eq!(catalog.generation, 1);
    assert_eq!(
        decode_schema(catalog.schema_bytes).unwrap().fields().len(),
        1
    );
    let document = decode_document(&recovered_document.value).expect("decode recovered document");
    assert_eq!(document.binding(), binding);
    assert_eq!(
        document.field(1).and_then(|value| value.as_str()),
        Some("survives reopen")
    );
}
