use std::fs;
use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::{SystemTime, UNIX_EPOCH};

use tb_core::{
    BudgetConfig, CollectionId, DatabaseId, MembershipEpoch, MemoryBudget, OriginId, ReplicaId,
    ShardId, TenantId, TransactionId, WorkerId,
};
use tb_document::{DocumentError, DocumentStore, DocumentStoreConfig, LocalSchemaBinding};
use tb_format::{FieldType, SchemaField, encode_catalog_into, encode_schema_into};
use tb_replication::{ConfigurationId, ReplicationLimits};
use tb_storage::{LocalDocumentMode, StorageConfig};

static NEXT_DIRECTORY: AtomicU64 = AtomicU64::new(0);

const TEST_TENANT: [u8; 16] = [51; 16];
const TEST_DATABASE: [u8; 16] = [52; 16];
const TEST_SHARD: u64 = 53;
const TEST_REPLICA: [u8; 16] = [54; 16];
const TEST_INCARNATION: [u8; 16] = [55; 16];
const TEST_EPOCH: u64 = 56;
const TEST_CONFIGURATION: [u8; 32] = [57; 32];

struct TempShard(PathBuf);

impl TempShard {
    fn new() -> Self {
        let nanos = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .expect("system clock is after the Unix epoch")
            .as_nanos();
        let ordinal = NEXT_DIRECTORY.fetch_add(1, Ordering::Relaxed);
        let path = std::env::temp_dir().join(format!(
            "terrabase-local-v1-contract-{}-{nanos}-{ordinal}",
            std::process::id()
        ));
        Self(path)
    }

    fn path(&self) -> &Path {
        &self.0
    }
}

impl Drop for TempShard {
    fn drop(&mut self) {
        if self.0.exists() {
            fs::remove_dir_all(&self.0).expect("remove temporary local-v1 shard");
        }
    }
}

fn tenant() -> TenantId {
    TenantId::from_bytes(TEST_TENANT)
}

fn local_mode() -> LocalDocumentMode {
    LocalDocumentMode::new(
        tenant(),
        DatabaseId::from_bytes(TEST_DATABASE),
        ShardId::new(TEST_SHARD),
        OriginId {
            replica_id: ReplicaId::from_bytes(TEST_REPLICA),
            incarnation: TEST_INCARNATION,
        },
        MembershipEpoch::new(TEST_EPOCH),
        TEST_CONFIGURATION,
    )
}

fn config(path: &Path) -> DocumentStoreConfig {
    DocumentStoreConfig {
        storage: StorageConfig::local(path, tenant(), WorkerId(9)).with_clock(Arc::new(|| 1_000)),
        database: DatabaseId::from_bytes(TEST_DATABASE),
        shard: ShardId::new(TEST_SHARD),
        origin: OriginId {
            replica_id: ReplicaId::from_bytes(TEST_REPLICA),
            incarnation: TEST_INCARNATION,
        },
        epoch: MembershipEpoch::new(TEST_EPOCH),
        configuration: ConfigurationId::from_bytes(TEST_CONFIGURATION)
            .expect("nonzero local configuration"),
        replication: ReplicationLimits {
            max_origins: 16,
            max_changes: 32,
            max_siblings: 16,
            max_value_bytes: tb_core::MAX_VALUE_BYTES,
            max_payload_bytes: tb_core::MAX_WAL_GROUP_BYTES,
        },
    }
}

fn budget() -> MemoryBudget {
    MemoryBudget::new(BudgetConfig::new(64 * 1024 * 1024, 0).expect("valid test budget"))
}

fn transaction(nonce: u8) -> TransactionId {
    TransactionId::new(50_000, [nonce; 16])
}

fn schema(field_id: u32, name: &'static str) -> Vec<u8> {
    let fields = [SchemaField {
        field_id,
        parent_field_id: 0,
        value_type: FieldType::Utf8,
        element_type: None,
        flags: 1,
        name,
    }];
    let mut bytes = Vec::new();
    encode_schema_into(&mut bytes, &fields).expect("encode valid local schema");
    bytes
}

fn expected_binding(
    collection: CollectionId,
    generation: u64,
    previous_generation: u64,
    schema_bytes: &[u8],
) -> LocalSchemaBinding {
    let mut catalog = Vec::new();
    let schema_digest = encode_catalog_into(
        &mut catalog,
        collection,
        generation,
        previous_generation,
        schema_bytes,
    )
    .expect("encode immutable catalog fixture");
    LocalSchemaBinding {
        collection,
        generation,
        schema_digest,
    }
}

fn provision(
    store: &mut DocumentStore,
    nonce: u8,
    collection: CollectionId,
    schema_bytes: &[u8],
) -> LocalSchemaBinding {
    let binding = expected_binding(collection, 1, 0, schema_bytes);
    assert!(
        store
            .provision_schema(transaction(nonce), collection, 1, 0, schema_bytes)
            .expect("provision immutable local schema")
            .is_some()
    );
    binding
}

#[test]
fn explicit_local_v1_bootstrap_provisions_then_reopens_exact_active_bindings() {
    let directory = TempShard::new();
    let config = config(directory.path());
    let mut store = DocumentStore::bootstrap_local_v1(config.clone(), budget(), local_mode())
        .expect("explicitly bootstrap local-v1 store");

    let first_collection = CollectionId::from_bytes([1; 16]);
    let second_collection = CollectionId::from_bytes([2; 16]);
    let first_schema = schema(1, "first_label");
    let second_schema = schema(7, "second_label");
    let expected = [
        provision(&mut store, 1, first_collection, &first_schema),
        provision(&mut store, 2, second_collection, &second_schema),
    ];
    assert!(expected[0].collection < expected[1].collection);
    drop(store);

    let reopened = DocumentStore::open_local_v1(config.clone(), budget(), local_mode(), &expected)
        .expect("strict reopen verifies every expected active binding");
    assert_eq!(reopened.snapshot().seq.get(), 2);
    drop(reopened);

    let unsorted = [expected[1], expected[0]];
    assert!(matches!(
        DocumentStore::open_local_v1(config.clone(), budget(), local_mode(), &unsorted,),
        Err(DocumentError::LocalCatalogMismatch)
    ));
    let duplicate = [expected[0], expected[0]];
    assert!(matches!(
        DocumentStore::open_local_v1(config, budget(), local_mode(), &duplicate),
        Err(DocumentError::LocalCatalogMismatch)
    ));
}

#[test]
fn local_v1_open_rejects_missing_active_pointer_and_mismatched_generation_or_digest() {
    let directory = TempShard::new();
    let config = config(directory.path());
    let store = DocumentStore::bootstrap_local_v1(config.clone(), budget(), local_mode())
        .expect("bootstrap empty local-v1 store");
    drop(store);

    let missing = LocalSchemaBinding {
        collection: CollectionId::from_bytes([3; 16]),
        generation: 1,
        schema_digest: [61; 32],
    };
    assert!(matches!(
        DocumentStore::open_local_v1(config.clone(), budget(), local_mode(), &[missing]),
        Err(DocumentError::LocalCatalogMismatch)
    ));

    assert!(DocumentStore::open_local_v1(config, budget(), local_mode(), &[]).is_err());
}

#[test]
fn local_v1_open_rejects_generation_and_digest_drift_from_active_schema() {
    let directory = TempShard::new();
    let config = config(directory.path());
    let collection = CollectionId::from_bytes([4; 16]);
    let schema_bytes = schema(1, "label");
    let mut store = DocumentStore::bootstrap_local_v1(config.clone(), budget(), local_mode())
        .expect("bootstrap local-v1 store");
    let expected = provision(&mut store, 4, collection, &schema_bytes);
    drop(store);

    let wrong_generation = LocalSchemaBinding {
        collection: expected.collection,
        generation: expected.generation + 1,
        schema_digest: expected.schema_digest,
    };
    assert!(matches!(
        DocumentStore::open_local_v1(config.clone(), budget(), local_mode(), &[wrong_generation],),
        Err(DocumentError::LocalCatalogMismatch)
    ));

    let mut wrong_digest_bytes = expected.schema_digest;
    wrong_digest_bytes[0] ^= 0xff;
    let wrong_digest = LocalSchemaBinding {
        collection: expected.collection,
        generation: expected.generation,
        schema_digest: wrong_digest_bytes,
    };
    assert!(matches!(
        DocumentStore::open_local_v1(config, budget(), local_mode(), &[wrong_digest],),
        Err(DocumentError::LocalCatalogMismatch)
    ));
}
