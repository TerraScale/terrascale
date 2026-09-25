use std::fs;
use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::{SystemTime, UNIX_EPOCH};

use tb_core::{
    BudgetConfig, CollectionId, DatabaseId, DocumentId, MemoryBudget, OriginId, ReplicaId, ShardId,
    TenantId, TransactionId, WorkerId,
};
use tb_document::{
    DocumentError, DocumentPoint, DocumentStore, DocumentStoreConfig, DocumentWrite,
    DocumentWriteValue,
};
use tb_format::{
    DocumentBinding, DocumentFieldInput, DocumentKind, FieldType, SchemaField, decode_document,
    encode_catalog_into, encode_schema_into,
};
use tb_replication::{
    CausalContext, ConfigurationId, DocumentValue, ReplicationLimits, verify_event,
};
use tb_storage::{StorageConfig, StorageError};

static NEXT_TEMP_DIRECTORY: AtomicU64 = AtomicU64::new(0);

struct TempShard {
    path: PathBuf,
}

impl TempShard {
    fn new() -> Self {
        let nanos = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .expect("system clock is after the Unix epoch")
            .as_nanos();
        let ordinal = NEXT_TEMP_DIRECTORY.fetch_add(1, Ordering::Relaxed);
        let path = std::env::temp_dir().join(format!(
            "terrabase-document-contract-{}-{nanos}-{ordinal}",
            std::process::id()
        ));
        Self { path }
    }

    fn path(&self) -> &Path {
        &self.path
    }
}

impl Drop for TempShard {
    fn drop(&mut self) {
        if self.path.exists() {
            fs::remove_dir_all(&self.path).expect("remove temporary document shard");
        }
    }
}

fn store_config(path: &Path) -> DocumentStoreConfig {
    DocumentStoreConfig {
        storage: StorageConfig::local(path, TenantId::from_bytes([40; 16]), WorkerId(9))
            .with_clock(Arc::new(|| 1_000)),
        database: DatabaseId::from_bytes([41; 16]),
        shard: ShardId::new(12),
        origin: OriginId {
            replica_id: ReplicaId::from_bytes([42; 16]),
            incarnation: [43; 16],
        },
        epoch: tb_core::MembershipEpoch::new(1),
        configuration: ConfigurationId::from_bytes([44; 32]).expect("nonzero test configuration"),
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
    MemoryBudget::new(BudgetConfig::new(64 * 1024 * 1024, 0).expect("valid document-test budget"))
}

fn transaction(nonce: u8) -> TransactionId {
    TransactionId::new(50_000, [nonce; 16])
}

fn string_schema() -> Vec<u8> {
    let fields = [SchemaField {
        field_id: 1,
        parent_field_id: 0,
        value_type: FieldType::Utf8,
        element_type: None,
        flags: 1,
        name: "label",
    }];
    let mut bytes = Vec::new();
    encode_schema_into(&mut bytes, &fields).expect("encode string schema");
    bytes
}

fn integer_schema() -> Vec<u8> {
    let fields = [SchemaField {
        field_id: 1,
        parent_field_id: 0,
        value_type: FieldType::I64,
        element_type: None,
        flags: 1,
        name: "count",
    }];
    let mut bytes = Vec::new();
    encode_schema_into(&mut bytes, &fields).expect("encode integer schema");
    bytes
}

fn provision(
    store: &mut DocumentStore,
    nonce: u8,
    collection: CollectionId,
    generation: u64,
    previous_generation: u64,
    schema: &[u8],
) -> [u8; 32] {
    let mut catalog = Vec::new();
    let digest = encode_catalog_into(
        &mut catalog,
        collection,
        generation,
        previous_generation,
        schema,
    )
    .expect("encode catalog fixture");
    let receipt = store
        .provision_schema(
            transaction(nonce),
            collection,
            generation,
            previous_generation,
            schema,
        )
        .expect("provision collection schema");
    assert!(receipt.is_some(), "new catalog generation is durable");
    digest
}

fn binding(
    collection: CollectionId,
    document: DocumentId,
    generation: u64,
    schema_digest: [u8; 32],
) -> DocumentBinding {
    DocumentBinding {
        collection,
        document,
        generation,
        schema_digest,
    }
}

fn string_field(value: &[u8]) -> [DocumentFieldInput<'_>; 1] {
    [DocumentFieldInput {
        field_id: 1,
        value_type: FieldType::Utf8,
        value,
    }]
}

#[test]
fn multi_collection_group_verifies_exact_wal_and_retries_after_reopen() {
    let directory = TempShard::new();
    let config = store_config(directory.path());
    let memory = budget();
    let mut store =
        DocumentStore::open(config.clone(), memory.clone()).expect("open document shard");

    let first_collection = CollectionId::from_bytes([1; 16]);
    let second_collection = CollectionId::from_bytes([2; 16]);
    let same_document = DocumentId::from_bytes([7; 16]);
    let first_digest = provision(&mut store, 1, first_collection, 1, 0, &string_schema());
    let second_digest = provision(&mut store, 2, second_collection, 1, 0, &integer_schema());
    assert_ne!(first_digest, second_digest);
    let before_group = store.snapshot();
    let first_binding = binding(first_collection, same_document, 1, first_digest);
    let second_binding = binding(second_collection, same_document, 1, second_digest);
    let no_prior_causality = CausalContext::new();
    let label = b"one atomic group";
    let count = 37_i64.to_le_bytes();
    let first_fields = string_field(label);
    let second_fields = [DocumentFieldInput {
        field_id: 1,
        value_type: FieldType::I64,
        value: &count,
    }];
    let writes = [
        DocumentWrite {
            binding: first_binding,
            observed: &no_prior_causality,
            value: DocumentWriteValue::Live(&first_fields),
        },
        DocumentWrite {
            binding: second_binding,
            observed: &no_prior_causality,
            value: DocumentWriteValue::Live(&second_fields),
        },
    ];
    let commit = store
        .commit_documents(transaction(3), &writes)
        .expect("commit same-ID changes across two collections atomically");
    assert_eq!(commit.event.sequence, 1);
    assert_eq!(
        commit.receipt.frontiers.durable,
        Some(commit.receipt.commit_seq)
    );
    assert_verified_two_collection_group(
        &commit,
        &config,
        first_collection,
        second_collection,
        same_document,
    );

    for collection in [first_collection, second_collection] {
        assert!(matches!(
            store
                .read(before_group, collection, same_document)
                .expect("read at pre-group snapshot")
                .point(),
            DocumentPoint::Absent
        ));
    }
    let committed_snapshot = store.snapshot();
    assert_live_string(
        &store,
        committed_snapshot,
        first_binding,
        commit.event,
        label,
    );
    assert_live_integer(&store, committed_snapshot, second_binding, commit.event, 37);
    drop(store);

    let mut store =
        DocumentStore::open(config.clone(), memory).expect("reopen durable document shard");
    let reopened_snapshot = store.snapshot();
    assert_live_string(
        &store,
        reopened_snapshot,
        first_binding,
        commit.event,
        label,
    );
    assert_live_integer(&store, reopened_snapshot, second_binding, commit.event, 37);
    assert_retry_resolution(
        &mut store,
        &writes,
        &no_prior_causality,
        &commit,
        first_binding,
        label,
    );
}

#[test]
fn local_value_tombstone_and_reused_causal_context_are_typed_at_the_facade() {
    let directory = TempShard::new();
    let config = store_config(directory.path());
    let mut store = DocumentStore::open(config.clone(), budget()).expect("open document shard");
    let collection = CollectionId::from_bytes([3; 16]);
    let document = DocumentId::from_bytes([8; 16]);
    let digest = provision(&mut store, 4, collection, 1, 0, &string_schema());
    let binding = binding(collection, document, 1, digest);
    let empty = CausalContext::new();
    let initial = b"before deletion";
    let fields = string_field(initial);
    let first = store
        .commit_documents(
            transaction(5),
            &[DocumentWrite {
                binding,
                observed: &empty,
                value: DocumentWriteValue::Live(&fields),
            }],
        )
        .expect("write live value");
    let first_snapshot = store.snapshot();
    let context = match store
        .read(first_snapshot, collection, document)
        .expect("read live value")
        .point()
    {
        DocumentPoint::Value { context, sibling } => {
            assert_eq!(sibling.kind, DocumentKind::Live);
            assert_eq!(sibling.event, first.event);
            assert_eq!(context.get(config.origin), first.event.sequence);
            assert_live_sibling(sibling, binding, initial);
            context.clone()
        }
        other => panic!("expected typed live value, got {other:?}"),
    };

    let deleted = store
        .commit_documents(
            transaction(6),
            &[DocumentWrite {
                binding,
                observed: &context,
                value: DocumentWriteValue::Tombstone,
            }],
        )
        .expect("delete using the context returned by the read");
    let deleted_snapshot = store.snapshot();
    let deletion_context = match store
        .read(deleted_snapshot, collection, document)
        .expect("read tombstone")
        .point()
    {
        DocumentPoint::Deleted { context, sibling } => {
            assert_eq!(sibling.kind, DocumentKind::Tombstone);
            assert_eq!(sibling.event, deleted.event);
            assert!(sibling.document_bytes.is_empty());
            assert_eq!(context.get(config.origin), deleted.event.sequence);
            context.clone()
        }
        other => panic!("expected typed tombstone, got {other:?}"),
    };

    let after_delete = b"after deletion";
    let fields = string_field(after_delete);
    let restored = store
        .commit_documents(
            transaction(7),
            &[DocumentWrite {
                binding,
                observed: &deletion_context,
                value: DocumentWriteValue::Live(&fields),
            }],
        )
        .expect("reuse returned tombstone context for a later live value");
    assert_eq!(restored.event.sequence, deleted.event.sequence + 1);
    assert_live_string(
        &store,
        store.snapshot(),
        binding,
        restored.event,
        after_delete,
    );
}

#[test]
fn stale_schema_generation_and_malformed_typed_input_fail_before_wal_append() {
    let directory = TempShard::new();
    let config = store_config(directory.path());
    let mut store = DocumentStore::open(config.clone(), budget()).expect("open document shard");
    let collection = CollectionId::from_bytes([4; 16]);
    let document = DocumentId::from_bytes([9; 16]);
    let first_schema = string_schema();
    let first_digest = provision(&mut store, 8, collection, 1, 0, &first_schema);
    let first_binding = binding(collection, document, 1, first_digest);
    let after_first_schema = store.snapshot();

    let stale_fields = string_field(b"stale binding");
    let integer_schema = integer_schema();
    let second_digest = {
        let mut catalog = Vec::new();
        encode_catalog_into(&mut catalog, collection, 2, 1, &integer_schema)
            .expect("encode second-generation catalog fixture")
    };
    store
        .provision_schema(transaction(9), collection, 2, 1, &integer_schema)
        .expect("advance active schema generation");
    let after_second_schema = store.snapshot();
    assert!(after_second_schema.seq > after_first_schema.seq);
    assert_ne!(first_digest, second_digest);
    assert!(matches!(
        store.commit_documents(
            transaction(10),
            &[DocumentWrite {
                binding: first_binding,
                observed: &CausalContext::new(),
                value: DocumentWriteValue::Live(&stale_fields),
            }],
        ),
        Err(DocumentError::SchemaChanged)
    ));
    assert_eq!(store.snapshot(), after_second_schema);
    assert!(matches!(
        store
            .read(store.snapshot(), collection, document)
            .expect("read after stale schema rejection")
            .point(),
        DocumentPoint::Absent
    ));

    let current_binding = binding(collection, document, 2, second_digest);
    let malformed_bytes = b"not an integer";
    let malformed_fields = [DocumentFieldInput {
        field_id: 1,
        value_type: FieldType::Utf8,
        value: malformed_bytes,
    }];
    let before_malformed = store.snapshot();
    assert!(matches!(
        store.commit_documents(
            transaction(11),
            &[DocumentWrite {
                binding: current_binding,
                observed: &CausalContext::new(),
                value: DocumentWriteValue::Live(&malformed_fields),
            }],
        ),
        Err(DocumentError::Format(_))
    ));
    assert_eq!(store.snapshot(), before_malformed);
    assert!(matches!(
        store
            .read(before_malformed, collection, document)
            .expect("read after malformed input rejection")
            .point(),
        DocumentPoint::Absent
    ));
}

fn assert_live_string(
    store: &DocumentStore,
    snapshot: tb_core::ReadSnapshot,
    binding: DocumentBinding,
    event: tb_format::DocumentEventId,
    expected: &[u8],
) {
    match store
        .read(snapshot, binding.collection, binding.document)
        .expect("read public document value")
        .point()
    {
        DocumentPoint::Value { sibling, .. } => {
            assert_eq!(sibling.event, event);
            assert_eq!(sibling.generation, binding.generation);
            assert_eq!(sibling.schema_digest, binding.schema_digest);
            assert_live_sibling(sibling, binding, expected);
        }
        other => panic!("expected public live value, got {other:?}"),
    }
}

fn assert_verified_two_collection_group(
    commit: &tb_document::DocumentCommit,
    config: &DocumentStoreConfig,
    first_collection: CollectionId,
    second_collection: CollectionId,
    document: DocumentId,
) {
    let verified = verify_event(
        commit.metadata(),
        commit.wal_frame(),
        tb_replication::EventDigest::from_bytes(commit.event_digest),
        config.replication,
    )
    .expect("verify producer metadata against its exact committed WAL frame");
    assert_eq!(verified.id().sequence(), 1);
    assert_eq!(verified.id().origin(), config.origin);
    assert_eq!(verified.transaction_id(), commit.receipt.transaction_id);
    assert_eq!(verified.bindings().len(), 2);
    assert_eq!(verified.changes().len(), 2);
    assert_eq!(verified.changes()[0].binding.collection, first_collection);
    assert_eq!(verified.changes()[1].binding.collection, second_collection);
    assert_eq!(verified.changes()[0].document, document);
    assert_eq!(verified.changes()[1].document, document);
    assert!(matches!(
        &verified.changes()[0].value,
        DocumentValue::Bytes(_)
    ));
    assert!(matches!(
        &verified.changes()[1].value,
        DocumentValue::Bytes(_)
    ));
}

fn assert_retry_resolution(
    store: &mut DocumentStore,
    original_writes: &[DocumentWrite<'_>; 2],
    no_prior_causality: &CausalContext,
    original_commit: &tb_document::DocumentCommit,
    first_binding: DocumentBinding,
    expected_label: &[u8],
) {
    let snapshot = store.snapshot();
    let retried = store
        .commit_documents(transaction(3), original_writes)
        .expect("same transaction and operations return the original durable event");
    assert_eq!(retried.receipt, original_commit.receipt);
    assert_eq!(retried.event, original_commit.event);
    assert_eq!(retried.event_digest, original_commit.event_digest);
    assert_eq!(store.snapshot(), snapshot);

    let changed_fields = string_field(b"different retry payload");
    let changed_writes = [
        DocumentWrite {
            binding: first_binding,
            observed: no_prior_causality,
            value: DocumentWriteValue::Live(&changed_fields),
        },
        original_writes[1],
    ];
    assert!(matches!(
        store.commit_documents(transaction(3), &changed_writes),
        Err(DocumentError::Storage(StorageError::IdempotencyConflict))
    ));
    assert_eq!(store.snapshot(), snapshot);
    assert_live_string(
        store,
        store.snapshot(),
        first_binding,
        original_commit.event,
        expected_label,
    );
}

fn assert_live_integer(
    store: &DocumentStore,
    snapshot: tb_core::ReadSnapshot,
    binding: DocumentBinding,
    event: tb_format::DocumentEventId,
    expected: i64,
) {
    match store
        .read(snapshot, binding.collection, binding.document)
        .expect("read integer document value")
        .point()
    {
        DocumentPoint::Value { sibling, .. } => {
            assert_eq!(sibling.event, event);
            assert_eq!(sibling.kind, DocumentKind::Live);
            let decoded = decode_document(&sibling.document_bytes).expect("decode integer value");
            assert_eq!(decoded.binding(), binding);
            let field = decoded.field(1).expect("integer field exists");
            assert_eq!(field.value_type(), FieldType::I64);
            assert_eq!(field.as_i64(), Some(expected));
        }
        other => panic!("expected integer value, got {other:?}"),
    }
}

fn assert_live_sibling(
    sibling: &tb_document::OwnedSibling,
    binding: DocumentBinding,
    expected: &[u8],
) {
    assert_eq!(sibling.kind, DocumentKind::Live);
    let decoded = decode_document(&sibling.document_bytes).expect("decode live sibling");
    assert_eq!(decoded.binding(), binding);
    let field = decoded.field(1).expect("string field exists");
    assert_eq!(field.value_type(), FieldType::Utf8);
    assert_eq!(field.as_str().map(str::as_bytes), Some(expected));
}
