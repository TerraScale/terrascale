use std::fs;
use std::path::PathBuf;
#[cfg(feature = "slow-tests")]
use std::process::Command;
use std::sync::Arc;
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::{SystemTime, UNIX_EPOCH};

use aws_lc_rs::signature::{Ed25519KeyPair, KeyPair};
use tb_core::{
    Attribution, BudgetConfig, CollectionId, CommitSeq, DatabaseId, DocumentId, Key, KeyBound,
    KeyRange, MemoryBudget, MemoryCategory, OriginId, ReplicaId, ShardId, TenantId, TransactionId,
    WorkerId,
};
use tb_document::{
    DocumentError, DocumentStoreConfig, DocumentWrite, DocumentWriteValue, open_distributed,
};
use tb_format::{
    DocumentBinding, DocumentDot, DocumentEventId, DocumentFieldInput, DocumentKind, FieldType,
    SchemaField, decode_causal_manifest, decode_state_pointer, describe_change,
    encode_catalog_into, encode_causal_manifest_into, encode_causal_manifest_key,
    encode_change_into, encode_change_key, encode_document_into, encode_schema_into,
};
use tb_replication::{
    AcceptedAuthority, AuthorityCertificateScope, AuthorityMemberInput, AuthorityPayload,
    AuthoritySchemaInput, AuthorityVersion, CausalContext, CollectionBinding, LocalAuthorityScope,
    ReplicationLimits, compute_event_digest, verify_event,
};
use tb_storage::{DistributedDocumentMode, Mutation, Storage, StorageConfig};

static NEXT_DIRECTORY: AtomicU64 = AtomicU64::new(0);
#[cfg(feature = "slow-tests")]
const SOURCE_CRASH_CHILD_ENV: &str = "TB_DOCUMENT_SOURCE_CRASH_CHILD";
#[cfg(feature = "slow-tests")]
const SOURCE_CRASH_DIRECTORY_ENV: &str = "TB_DOCUMENT_SOURCE_CRASH_DIRECTORY";
#[cfg(feature = "slow-tests")]
const SOURCE_CRASH_EXECUTED_ENV: &str = "TB_DOCUMENT_SOURCE_CRASH_EXECUTED";
const CLUSTER: [u8; 16] = [9; 16];
const TENANT: TenantId = TenantId::from_bytes([1; 16]);
const DATABASE: DatabaseId = DatabaseId::from_bytes([2; 16]);
const SHARD: ShardId = ShardId::new(1);
const LOCAL_ORIGIN: OriginId = OriginId {
    replica_id: ReplicaId::from_bytes([3; 16]),
    incarnation: [4; 16],
};

struct TempShard(PathBuf);

impl TempShard {
    fn new() -> Self {
        let nanos = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .expect("clock after epoch")
            .as_nanos();
        Self(std::env::temp_dir().join(format!(
            "terrabase-distributed-open-{}-{nanos}-{}",
            std::process::id(),
            NEXT_DIRECTORY.fetch_add(1, Ordering::Relaxed)
        )))
    }
}

impl Drop for TempShard {
    fn drop(&mut self) {
        if self.0.exists() {
            fs::remove_dir_all(&self.0).expect("remove test shard");
        }
    }
}

fn budget() -> MemoryBudget {
    MemoryBudget::new(BudgetConfig::new(256 * 1024 * 1024, 0).expect("test budget"))
}

fn test_schema(field_count: usize) -> Vec<u8> {
    let names: Vec<String> = (0..field_count)
        .map(|index| format!("{index:04}{}", "x".repeat(124)))
        .collect();
    let fields: Vec<_> = names
        .iter()
        .enumerate()
        .map(|(index, name)| SchemaField {
            field_id: u32::try_from(index + 1).expect("bounded field ID"),
            parent_field_id: 0,
            value_type: FieldType::Utf8,
            element_type: None,
            flags: 0,
            name,
        })
        .collect();
    let mut schema = Vec::new();
    encode_schema_into(&mut schema, &fields).expect("canonical schema");
    schema
}

fn fixture_catalogs(field_counts: &[usize]) -> Vec<(CollectionBinding, Vec<u8>)> {
    field_counts
        .iter()
        .enumerate()
        .map(|(index, field_count)| {
            let collection = CollectionId::from_bytes([10 + u8::try_from(index).unwrap(); 16]);
            let schema = test_schema(*field_count);
            let mut catalog = Vec::new();
            let digest = encode_catalog_into(&mut catalog, collection, 1, 0, &schema)
                .expect("canonical catalog");
            (
                CollectionBinding {
                    collection,
                    generation: 1,
                    schema_digest: digest,
                },
                catalog,
            )
        })
        .collect()
}

fn install_test_authority(
    directory: &TempShard,
    version: AuthorityVersion,
    catalogs: &[(CollectionBinding, Vec<u8>)],
) -> (Arc<AcceptedAuthority>, MemoryBudget) {
    let members = [
        AuthorityMemberInput {
            origin: LOCAL_ORIGIN,
            leaf_der: b"local-test-leaf",
            server_name: "local.test",
            failure_domain: "zone-a",
        },
        AuthorityMemberInput {
            origin: OriginId {
                replica_id: ReplicaId::from_bytes([5; 16]),
                incarnation: [6; 16],
            },
            leaf_der: b"peer-test-leaf",
            server_name: "peer.test",
            failure_domain: "zone-b",
        },
    ];
    let schemas: Vec<_> = catalogs
        .iter()
        .map(|(binding, catalog)| AuthoritySchemaInput {
            binding: *binding,
            catalog,
        })
        .collect();
    let active: Vec<_> = catalogs.iter().map(|(binding, _)| *binding).collect();
    let scope = AuthorityCertificateScope {
        cluster: CLUSTER,
        tenant: TENANT,
        database: DATABASE,
        shard: SHARD,
        epoch: tb_core::MembershipEpoch::new(1),
    };
    let payload = match version {
        AuthorityVersion::V1 => AuthorityPayload::encode(scope, &members, &schemas),
        AuthorityVersion::V2 => AuthorityPayload::encode_v2(scope, &members, &schemas, &active),
    }
    .expect("canonical authority payload");
    let key = Ed25519KeyPair::from_seed_unchecked(&[7; 32]).expect("fixed test key");
    let public_key: [u8; 32] = key.public_key().as_ref().try_into().expect("key width");
    let signature: [u8; 64] = key
        .sign(&payload.signing_input().expect("signing bytes"))
        .as_ref()
        .try_into()
        .expect("signature width");
    let certificate = payload
        .finish(&signature, &public_key)
        .expect("signed authority certificate");
    let memory = budget();
    let authority = Arc::new(
        AcceptedAuthority::open_or_install(
            &directory.0,
            &certificate,
            &public_key,
            LocalAuthorityScope {
                cluster: CLUSTER,
                tenant: TENANT,
                database: DATABASE,
                shard: SHARD,
                member: LOCAL_ORIGIN.replica_id,
            },
            &memory,
            Attribution {
                tenant: TENANT,
                worker: WorkerId(1),
                category: MemoryCategory::Replication,
            },
        )
        .expect("accepted signed authority"),
    );
    (authority, memory)
}

struct Fixture {
    authority: Arc<AcceptedAuthority>,
    config: DocumentStoreConfig,
    memory: MemoryBudget,
    catalogs: Vec<(CollectionBinding, Vec<u8>)>,
    #[cfg_attr(
        not(feature = "slow-tests"),
        allow(
            dead_code,
            reason = "retains the fixture cleanup guard when crash hooks are disabled"
        )
    )]
    directory: Option<TempShard>,
}

#[derive(Debug, Eq, PartialEq)]
struct VisibleRow {
    key: Vec<u8>,
    value: Vec<u8>,
}

impl Fixture {
    fn new(version: AuthorityVersion, field_counts: &[usize]) -> Self {
        Self::new_in(TempShard::new(), version, field_counts)
    }

    fn new_in(directory: TempShard, version: AuthorityVersion, field_counts: &[usize]) -> Self {
        let catalogs = fixture_catalogs(field_counts);
        let (authority, memory) = install_test_authority(&directory, version, &catalogs);
        let clock = Arc::new(AtomicU64::new(1_000));
        let configured_clock = Arc::clone(&clock);
        let storage = StorageConfig::local(authority.view().documents_path(), TENANT, WorkerId(1))
            .with_clock(Arc::new(move || configured_clock.load(Ordering::SeqCst)));
        let config = DocumentStoreConfig {
            storage,
            database: DATABASE,
            shard: SHARD,
            origin: LOCAL_ORIGIN,
            epoch: tb_core::MembershipEpoch::new(1),
            configuration: authority.view().configuration(),
            replication: ReplicationLimits {
                max_origins: 16,
                max_changes: 32,
                max_siblings: 16,
                max_value_bytes: tb_core::MAX_VALUE_BYTES,
                max_payload_bytes: tb_core::MAX_WAL_GROUP_BYTES,
            },
        };
        Self {
            authority,
            config,
            memory,
            catalogs,
            directory: Some(directory),
        }
    }

    fn mode(&self) -> DistributedDocumentMode {
        let view = self.authority.view();
        DistributedDocumentMode::new(
            view.cluster(),
            view.tenant(),
            view.database(),
            view.shard(),
            view.epoch(),
            view.configuration().to_bytes(),
            view.local_origin(),
        )
    }

    fn raw_storage(&self) -> Storage {
        Storage::open_distributed_document(
            self.config.storage.clone(),
            self.memory.clone(),
            self.mode(),
        )
        .expect("trusted test storage open")
    }

    fn seed_distributed(&self, mutations: &[Mutation]) {
        let storage = self.raw_storage();
        storage
            .commit_batch(
                storage.snapshot(),
                TransactionId::new(50_000, [8; 16]),
                mutations,
            )
            .expect("seed marked shard");
    }

    fn visible(&self) -> (u64, Vec<VisibleRow>) {
        let mut inspect_config = self.config.storage.clone();
        inspect_config.max_scan_rows = 4_096;
        inspect_config.max_scan_bytes = 16 * 1024 * 1024;
        let storage =
            Storage::open_distributed_document(inspect_config.clone(), budget(), self.mode())
                .expect("inspect marked shard");
        let stats = storage.stats();
        let page = storage
            .scan(
                storage.snapshot(),
                &KeyRange::new(KeyBound::Unbounded, KeyBound::Unbounded).expect("full range"),
                inspect_config.max_scan_rows,
            )
            .expect("inspect visible rows");
        let rows = page
            .rows()
            .iter()
            .map(|row| VisibleRow {
                key: row.key.as_bytes().to_vec(),
                value: row.value.clone(),
            })
            .collect();
        (stats.visible.get(), rows)
    }

    fn open(&self) -> Result<tb_document::DistributedDocumentStore, DocumentError> {
        open_distributed(
            Arc::clone(&self.authority),
            self.config.clone(),
            self.memory.clone(),
        )
    }
}

#[test]
fn certified_source_stages_d6_then_publishes_exact_v2_event_and_retries() {
    let fixture = Fixture::new(AuthorityVersion::V2, &[1]);
    let store = fixture.open().expect("open certified shard");
    let collection = fixture.catalogs[0].0.collection;
    let document = DocumentId::from_bytes([17; 16]);
    let binding = DocumentBinding {
        collection,
        document,
        generation: fixture.catalogs[0].0.generation,
        schema_digest: fixture.catalogs[0].0.schema_digest,
    };
    let fields = [DocumentFieldInput {
        field_id: 1,
        value_type: FieldType::Utf8,
        value: b"signed source",
    }];
    let empty = CausalContext::new();
    let write = DocumentWrite {
        binding,
        observed: &empty,
        value: DocumentWriteValue::Live(&fields),
    };
    let token = TransactionId::new(50_000, [19; 16]);
    let first = store
        .commit_documents(token, &[write])
        .expect("source event");
    assert_eq!(first.event.sequence, 1);
    let digest = compute_event_digest(first.metadata(), first.wal_frame()).expect("event digest");
    assert_eq!(digest.to_bytes(), first.event_digest);
    let verified = verify_event(
        first.metadata(),
        first.wal_frame(),
        digest,
        fixture.config.replication,
    )
    .expect("semantic source proof");
    assert_eq!(verified.id().sequence(), 1);
    let resend = store
        .next_source_after(0)
        .expect("resend scan")
        .expect("source event");
    assert_eq!(resend.event, first.event);
    assert_eq!(resend.metadata(), first.metadata());
    assert_eq!(resend.wal_frame(), first.wal_frame());
    assert!(store.next_source_after(1).expect("resend end").is_none());
    drop(resend);
    drop(first);
    drop(store);

    let (visible_sequence, rows) = fixture.visible();
    assert_eq!(visible_sequence, 3, "C0, pure D6 stage, final event");
    let pointer_row = rows
        .iter()
        .find(|row| row.key.first() == Some(&0xD0))
        .expect("visible D0");
    let pointer = decode_state_pointer(&pointer_row.key, &pointer_row.value).expect("v2 pointer");
    let manifest_row = rows
        .iter()
        .find(|row| row.key.first() == Some(&0xD6))
        .expect("visible D6");
    let manifest = decode_causal_manifest(
        &manifest_row.key,
        &manifest_row.value,
        DATABASE,
        SHARD,
        16,
        16,
    )
    .expect("bounded D6");
    assert_eq!(pointer.manifest_sha256, manifest.sha256());
    assert_eq!(manifest.siblings().len(), 1);
    assert_eq!(manifest.context()[0].sequence, 1);

    let reopened = fixture.open().expect("source WAL and D0 chain audit");
    let retried = reopened
        .commit_documents(token, &[write])
        .expect("same caller retry");
    assert_eq!(retried.event.sequence, 1);
    assert_eq!(retried.receipt.commit_seq.get(), 3);
    drop(retried);
    drop(reopened);
    assert_eq!(fixture.visible().0, 3, "retry adds no stage or event");
}

#[test]
fn distributed_source_uses_signed_active_binding() {
    let fixture = Fixture::new(AuthorityVersion::V2, &[1]);
    let store = fixture.open().expect("certified C0");
    let binding = fixture.catalogs[0].0;
    let forged = DocumentBinding {
        collection: binding.collection,
        document: DocumentId::from_bytes([18; 16]),
        generation: binding.generation,
        schema_digest: [0xAA; 32],
    };
    assert!(matches!(
        store.commit_documents(
            TransactionId::new(50_000, [20; 16]),
            &[DocumentWrite {
                binding: forged,
                observed: &CausalContext::new(),
                value: DocumentWriteValue::Tombstone,
            }]
        ),
        Err(DocumentError::SchemaChanged)
    ));
}

fn prestage_tombstone(fixture: &Fixture, document: DocumentId) -> DocumentBinding {
    let signed = fixture.catalogs[0].0;
    let binding = DocumentBinding {
        collection: signed.collection,
        document,
        generation: signed.generation,
        schema_digest: signed.schema_digest,
    };
    let event = DocumentEventId {
        database: DATABASE,
        shard: SHARD,
        origin: LOCAL_ORIGIN,
        sequence: 1,
    };
    let change_key = encode_change_key(signed.collection, document, LOCAL_ORIGIN, 1, 0)
        .expect("canonical D1 key");
    let mut change_value = Vec::new();
    encode_change_into(
        &mut change_value,
        event,
        binding,
        DocumentKind::Tombstone,
        &[],
    )
    .expect("canonical D1 value");
    let descriptor = describe_change(&change_key, &change_value).expect("D1 descriptor");
    let mut manifest_value = Vec::new();
    let hash = encode_causal_manifest_into(
        &mut manifest_value,
        signed.collection,
        document,
        DATABASE,
        SHARD,
        &[DocumentDot {
            origin: LOCAL_ORIGIN,
            sequence: 1,
        }],
        &[descriptor],
        16,
        16,
    )
    .expect("canonical D6");
    let manifest_key = encode_causal_manifest_key(signed.collection, document, hash);
    fixture.seed_distributed(&[Mutation::Put {
        key: Key::new(manifest_key.to_vec()).expect("D6 key"),
        value: manifest_value,
    }]);
    binding
}

#[test]
fn preexisting_exact_d6_is_reused_after_reopen_without_another_stage() {
    let fixture = Fixture::new(AuthorityVersion::V2, &[1]);
    drop(fixture.open().expect("install signed C0"));
    let binding = prestage_tombstone(&fixture, DocumentId::from_bytes([21; 16]));
    assert_eq!(fixture.visible().0, 2, "C0 then orphan D6");
    let store = fixture.open().expect("orphan D6 is inert on reopen");
    let committed = store
        .commit_documents(
            TransactionId::new(50_000, [22; 16]),
            &[DocumentWrite {
                binding,
                observed: &CausalContext::new(),
                value: DocumentWriteValue::Tombstone,
            }],
        )
        .expect("reuse exact D6 and publish source");
    assert_eq!(committed.receipt.commit_seq.get(), 3, "no new D6 stage");
    drop(committed);
    drop(store);
    assert_eq!(fixture.visible().0, 3);
    assert!(fixture.open().is_ok(), "D0 to reused D6 to D1 audits");
}

#[test]
fn content_address_mismatch_in_retained_d6_refuses_reopen() {
    let fixture = Fixture::new(AuthorityVersion::V2, &[1]);
    drop(fixture.open().expect("install signed C0"));
    let key_bytes = encode_causal_manifest_key(
        fixture.catalogs[0].0.collection,
        DocumentId::from_bytes([22; 16]),
        [0xAA; 32],
    );
    fixture.seed_distributed(&[Mutation::Put {
        key: Key::new(key_bytes.to_vec()).expect("D6 key"),
        value: vec![0xBB],
    }]);
    assert!(
        fixture.open().is_err(),
        "malformed hash-keyed D6 is never reused"
    );
}

#[test]
fn valid_orphan_d6_with_unsigned_descriptor_refuses_reopen() {
    let fixture = Fixture::new(AuthorityVersion::V2, &[1]);
    drop(fixture.open().expect("install signed C0"));
    let signed = fixture.catalogs[0].0;
    let document = DocumentId::from_bytes([25; 16]);
    let forged = DocumentBinding {
        collection: signed.collection,
        document,
        generation: signed.generation,
        schema_digest: [0xBB; 32],
    };
    let event = DocumentEventId {
        database: DATABASE,
        shard: SHARD,
        origin: LOCAL_ORIGIN,
        sequence: 1,
    };
    let change_key = encode_change_key(signed.collection, document, LOCAL_ORIGIN, 1, 0)
        .expect("canonical D1 key");
    let mut change_value = Vec::new();
    encode_change_into(
        &mut change_value,
        event,
        forged,
        DocumentKind::Tombstone,
        &[],
    )
    .expect("canonical D1 with unsigned binding");
    let descriptor = describe_change(&change_key, &change_value).expect("D1 descriptor");
    let mut manifest_value = Vec::new();
    let hash = encode_causal_manifest_into(
        &mut manifest_value,
        signed.collection,
        document,
        DATABASE,
        SHARD,
        &[DocumentDot {
            origin: LOCAL_ORIGIN,
            sequence: 1,
        }],
        &[descriptor],
        16,
        16,
    )
    .expect("hash- and CRC-valid orphan D6");
    fixture.seed_distributed(&[Mutation::Put {
        key: Key::new(encode_causal_manifest_key(signed.collection, document, hash).to_vec())
            .expect("D6 key"),
        value: manifest_value,
    }]);
    assert!(matches!(
        fixture.open(),
        Err(DocumentError::Corrupt("signed schema digest mismatch"))
    ));
}

#[test]
fn whole_operation_reservation_refuses_before_first_d6_stage() {
    let mut fixture = Fixture::new(AuthorityVersion::V2, &[1]);
    drop(fixture.open().expect("install signed C0"));
    fixture.config.storage.max_live_transactions = 2;
    let store = fixture.open().expect("reopen within live-token quota");
    let signed = fixture.catalogs[0].0;
    let result = store.commit_documents(
        TransactionId::new(50_000, [23; 16]),
        &[DocumentWrite {
            binding: DocumentBinding {
                collection: signed.collection,
                document: DocumentId::from_bytes([23; 16]),
                generation: signed.generation,
                schema_digest: signed.schema_digest,
            },
            observed: &CausalContext::new(),
            value: DocumentWriteValue::Tombstone,
        }],
    );
    assert!(matches!(
        result,
        Err(DocumentError::Storage(tb_storage::StorageError::Capacity(
            "live transaction resolution table"
        )))
    ));
    drop(store);
    assert_eq!(fixture.visible().0, 1, "reservation refusal wrote no D6");
}

fn one_byte_live_field() -> [DocumentFieldInput<'static>; 1] {
    [DocumentFieldInput {
        field_id: 1,
        value_type: FieldType::Utf8,
        value: b"x",
    }]
}

fn individual_value_limit_refuses_before_staging() {
    let mut single = Fixture::new(AuthorityVersion::V2, &[1]);
    drop(single.open().expect("install signed C0"));
    single.config.replication.max_value_bytes = 1;
    single.config.replication.max_payload_bytes = 1;
    let signed = single.catalogs[0].0;
    let binding = DocumentBinding {
        collection: signed.collection,
        document: DocumentId::from_bytes([26; 16]),
        generation: signed.generation,
        schema_digest: signed.schema_digest,
    };
    let fields = one_byte_live_field();
    let empty = CausalContext::new();
    let store = single.open().expect("narrowed source shard");
    assert!(matches!(
        store.commit_documents(
            TransactionId::new(50_000, [33; 16]),
            &[DocumentWrite {
                binding,
                observed: &empty,
                value: DocumentWriteValue::Live(&fields),
            }]
        ),
        Err(DocumentError::Capacity("document value bytes"))
    ));
    assert!(store.next_source_after(0).expect("no event").is_none());
    let valid = store
        .commit_documents(
            TransactionId::new(50_000, [34; 16]),
            &[DocumentWrite {
                binding,
                observed: &empty,
                value: DocumentWriteValue::Tombstone,
            }],
        )
        .expect("same writer accepts later valid tombstone");
    assert_eq!(valid.event.sequence, 1);
    assert_eq!(
        valid.receipt.commit_seq.get(),
        3,
        "individual limit refusal left physical WAL at C0"
    );
    drop(valid);
    drop(store);
    assert_eq!(single.visible().0, 3);
    assert!(single.open().is_ok(), "later valid source reopens");
}

fn aggregate_payload_limit_refuses_before_staging() {
    let mut aggregate = Fixture::new(AuthorityVersion::V2, &[1]);
    drop(aggregate.open().expect("install signed C0"));
    let empty = CausalContext::new();
    let signed = aggregate.catalogs[0].0;
    let first_binding = DocumentBinding {
        collection: signed.collection,
        document: DocumentId::from_bytes([27; 16]),
        generation: signed.generation,
        schema_digest: signed.schema_digest,
    };
    let second_binding = DocumentBinding {
        document: DocumentId::from_bytes([28; 16]),
        ..first_binding
    };
    let fields = one_byte_live_field();
    let mut encoded = Vec::new();
    encode_document_into(&mut encoded, first_binding, &fields)
        .expect("encoded unit used by replication");
    aggregate.config.replication.max_value_bytes = encoded.len();
    aggregate.config.replication.max_payload_bytes = encoded.len() * 2 - 1;
    let store = aggregate.open().expect("aggregate-limited source shard");
    let result = store.commit_documents(
        TransactionId::new(50_000, [35; 16]),
        &[
            DocumentWrite {
                binding: first_binding,
                observed: &empty,
                value: DocumentWriteValue::Live(&fields),
            },
            DocumentWrite {
                binding: second_binding,
                observed: &empty,
                value: DocumentWriteValue::Live(&fields),
            },
        ],
    );
    assert!(matches!(
        result,
        Err(DocumentError::Capacity("event payload bytes"))
    ));
    assert!(store.next_source_after(0).expect("no event").is_none());
    let valid = store
        .commit_documents(
            TransactionId::new(50_000, [36; 16]),
            &[DocumentWrite {
                binding: first_binding,
                observed: &empty,
                value: DocumentWriteValue::Live(&fields),
            }],
        )
        .expect("same writer accepts later valid single value");
    assert_eq!(valid.event.sequence, 1);
    assert_eq!(
        valid.receipt.commit_seq.get(),
        3,
        "aggregate limit refusal left physical WAL at C0"
    );
    drop(valid);
    drop(store);
    assert_eq!(aggregate.visible().0, 3);
    assert!(aggregate.open().is_ok(), "later valid source reopens");
}

#[test]
fn narrowed_value_and_aggregate_payload_limits_reject_before_any_stage() {
    individual_value_limit_refuses_before_staging();
    aggregate_payload_limit_refuses_before_staging();
}

#[test]
fn reachable_multi_document_source_uses_bounded_stage_packer() {
    let fixture = Fixture::new(AuthorityVersion::V2, &[1]);
    let store = fixture.open().expect("certified source shard");
    let signed = fixture.catalogs[0].0;
    let empty = CausalContext::new();
    let writes = (1_u8..=16)
        .map(|index| DocumentWrite {
            binding: DocumentBinding {
                collection: signed.collection,
                document: DocumentId::from_bytes([index; 16]),
                generation: signed.generation,
                schema_digest: signed.schema_digest,
            },
            observed: &empty,
            value: DocumentWriteValue::Tombstone,
        })
        .collect::<Vec<_>>();
    let commit = store
        .commit_documents(TransactionId::new(50_000, [24; 16]), &writes)
        .expect("16 sorted D1 and D0 in one source group");
    assert_eq!(commit.event.sequence, 1);
    assert_eq!(commit.receipt.commit_seq.get(), 3);
    drop(commit);
    drop(store);
    let (physical_sequence, rows) = fixture.visible();
    assert_eq!(
        physical_sequence, 3,
        "one C0, one D6 stage, one source event"
    );
    assert_eq!(
        rows.iter()
            .filter(|row| row.key.first() == Some(&0xD6))
            .count(),
        16
    );
    assert_eq!(
        rows.iter()
            .filter(|row| row.key.first() == Some(&0xD1))
            .count(),
        16
    );
    assert_eq!(
        rows.iter()
            .filter(|row| row.key.first() == Some(&0xD0))
            .count(),
        16
    );
    assert!(
        fixture.open().is_ok(),
        "all 16 D0 to D6 to D1 chains reopen"
    );
}

#[test]
fn resend_orders_multiple_sources_across_interleaved_d6_stages() {
    let fixture = Fixture::new(AuthorityVersion::V2, &[1]);
    let store = fixture.open().expect("signed source shard");
    let signed = fixture.catalogs[0].0;
    let first = DocumentBinding {
        collection: signed.collection,
        document: DocumentId::from_bytes([40; 16]),
        generation: signed.generation,
        schema_digest: signed.schema_digest,
    };
    let second = DocumentBinding {
        document: DocumentId::from_bytes([41; 16]),
        ..first
    };
    for (nonce, binding) in [(40_u8, first), (41_u8, second), (42_u8, first)] {
        let receipt = store
            .commit_documents(
                TransactionId::new(50_000, [nonce; 16]),
                &[DocumentWrite {
                    binding,
                    observed: &CausalContext::new(),
                    value: DocumentWriteValue::Tombstone,
                }],
            )
            .expect("one staged source event");
        assert_eq!(receipt.event.sequence, u64::from(nonce - 39));
    }
    drop(store);
    assert_eq!(fixture.visible().0, 7, "C0 then three D6/event pairs");
    let reopened = fixture.open().expect("audit every interleaved group");
    for after in 0..3 {
        let frame = reopened
            .next_source_after(after)
            .expect("resend cursor")
            .expect("next source, skipping C0 and D6");
        assert_eq!(frame.event.sequence, after + 1);
        let digest =
            compute_event_digest(frame.metadata(), frame.wal_frame()).expect("exact digest");
        assert_eq!(digest.to_bytes(), frame.event_digest);
    }
    assert!(reopened.next_source_after(3).expect("end cursor").is_none());
}

#[test]
fn near_one_mebibyte_live_d1_commits_and_reopens_with_bounded_reservations() {
    let fixture = Fixture::new(AuthorityVersion::V2, &[1]);
    let store = fixture.open().expect("signed source shard");
    let signed = fixture.catalogs[0].0;
    let binding = DocumentBinding {
        collection: signed.collection,
        document: DocumentId::from_bytes([43; 16]),
        generation: signed.generation,
        schema_digest: signed.schema_digest,
    };
    let value = vec![b'x'; 1_047_000];
    let fields = [DocumentFieldInput {
        field_id: 1,
        value_type: FieldType::Utf8,
        value: &value,
    }];
    let before = fixture.memory.snapshot();
    let commit = store
        .commit_documents(
            TransactionId::new(50_000, [43; 16]),
            &[DocumentWrite {
                binding,
                observed: &CausalContext::new(),
                value: DocumentWriteValue::Live(&fields),
            }],
        )
        .expect("near-limit D1 source under shared 256 MiB controller");
    assert_eq!(commit.event.sequence, 1);
    let held = fixture.memory.snapshot();
    assert!(
        held.reserved_bytes >= commit.wal_frame().len(),
        "retained exact frame remains covered by a budget permit"
    );
    assert!(
        held.reserved_bytes <= held.usable_limit_bytes,
        "observed reservation use remains under configured process budget"
    );
    assert!(held.reserved_bytes >= before.reserved_bytes);
    assert_eq!(held.usable_limit_bytes, 256 * 1024 * 1024);
    drop(commit);
    drop(store);
    let (physical, rows) = fixture.visible();
    assert_eq!(physical, 3);
    let d1 = rows
        .iter()
        .find(|row| row.key.first() == Some(&0xD1))
        .expect("visible D1");
    assert!(d1.value.len() > 1_000_000 && d1.value.len() <= tb_core::MAX_VALUE_BYTES);
    assert!(fixture.open().is_ok(), "large D1 chain audits on reopen");
}

#[test]
fn source_operation_and_frame_byte_caps_refuse_without_a_d6_stage() {
    for frame_bound in [false, true] {
        let mut fixture = Fixture::new(AuthorityVersion::V2, &[1]);
        drop(fixture.open().expect("install signed C0"));
        let default_format = fixture.config.storage.format;
        if frame_bound {
            let storage = fixture.raw_storage();
            let c0_len = storage
                .committed_cursor()
                .expect("C0 cursor")
                .next(CommitSeq::ZERO, default_format.max_frame_bytes)
                .expect("C0 WAL read")
                .expect("C0 frame")
                .encoded_bytes()
                .len();
            drop(storage);
            fixture.config.storage.format.max_frame_bytes = c0_len.max(512);
        } else {
            fixture.config.storage.format.max_operations = 4;
        }
        let signed = fixture.catalogs[0].0;
        let binding = DocumentBinding {
            collection: signed.collection,
            document: DocumentId::from_bytes([44; 16]),
            generation: signed.generation,
            schema_digest: signed.schema_digest,
        };
        let store = fixture.open().expect("reopen under narrowed format cap");
        assert!(
            store
                .commit_documents(
                    TransactionId::new(50_000, [44; 16]),
                    &[DocumentWrite {
                        binding,
                        observed: &CausalContext::new(),
                        value: DocumentWriteValue::Tombstone,
                    }]
                )
                .is_err()
        );
        assert!(
            store
                .next_source_after(0)
                .expect("no source after refusal")
                .is_none()
        );
        drop(store);
        assert_eq!(
            fixture.visible().0,
            1,
            "format refusal wrote no D6 or final"
        );
        fixture.config.storage.format = default_format;
        let store = fixture.open().expect("restore format cap");
        let valid = store
            .commit_documents(
                TransactionId::new(50_000, [45; 16]),
                &[DocumentWrite {
                    binding,
                    observed: &CausalContext::new(),
                    value: DocumentWriteValue::Tombstone,
                }],
            )
            .expect("valid source after format refusal");
        assert_eq!(valid.receipt.commit_seq.get(), 3);
        drop(valid);
        drop(store);
        assert_eq!(fixture.visible().0, 3);
    }
}

#[test]
fn source_item_reservation_refusal_precedes_d6_and_releases_cleanly() {
    let fixture = Fixture::new(AuthorityVersion::V2, &[1]);
    drop(fixture.open().expect("install signed C0"));
    let signed = fixture.catalogs[0].0;
    let binding = DocumentBinding {
        collection: signed.collection,
        document: DocumentId::from_bytes([46; 16]),
        generation: signed.generation,
        schema_digest: signed.schema_digest,
    };
    let store = fixture.open().expect("open under item controller");
    let used = fixture
        .memory
        .snapshot()
        .worker_items
        .iter()
        .find(|(tenant, worker, _)| *tenant == TENANT && *worker == WorkerId(1))
        .map_or(0, |(_, _, count)| *count);
    let held_items = fixture
        .memory
        .try_reserve_items(
            Attribution {
                tenant: TENANT,
                worker: WorkerId(1),
                category: MemoryCategory::Query,
            },
            tb_core::DEFAULT_MAX_WORKER_ITEMS
                .checked_sub(used)
                .expect("room in worker item budget"),
        )
        .expect("fill remaining item capacity");
    let result = store.commit_documents(
        TransactionId::new(50_000, [46; 16]),
        &[DocumentWrite {
            binding,
            observed: &CausalContext::new(),
            value: DocumentWriteValue::Tombstone,
        }],
    );
    assert!(matches!(
        result,
        Err(DocumentError::Storage(tb_storage::StorageError::Admission(
            tb_core::AdmissionError::ItemLimitExceeded { .. }
        )))
    ));
    assert!(
        store
            .next_source_after(0)
            .expect("no source after refusal")
            .is_none()
    );
    drop(held_items);
    let valid = store
        .commit_documents(
            TransactionId::new(50_000, [47; 16]),
            &[DocumentWrite {
                binding,
                observed: &CausalContext::new(),
                value: DocumentWriteValue::Tombstone,
            }],
        )
        .expect("same writer resumes after item permit release");
    assert_eq!(
        valid.receipt.commit_seq.get(),
        3,
        "no stage written before item refusal"
    );
    drop(valid);
    drop(store);
    assert_eq!(fixture.visible().0, 3);
}

#[test]
fn source_disk_envelope_refuses_whole_plan_before_d6() {
    let mut fixture = Fixture::new(AuthorityVersion::V2, &[1]);
    drop(fixture.open().expect("install signed C0"));
    let default_format = fixture.config.storage.format;
    let default_disk = fixture.config.storage.max_disk_bytes;
    let storage = fixture.raw_storage();
    let c0_len = storage
        .committed_cursor()
        .expect("C0 cursor")
        .next(CommitSeq::ZERO, default_format.max_frame_bytes)
        .expect("C0 WAL read")
        .expect("C0 frame")
        .encoded_bytes()
        .len();
    let disk_bytes = storage.stats().disk_bytes;
    drop(storage);
    fixture.config.storage.format.max_frame_bytes = c0_len.max(2_048);
    fixture.config.storage.max_disk_bytes = disk_bytes
        .checked_add(1)
        .expect("disk bound")
        .max(fixture.config.storage.format.max_frame_bytes as u64);
    let signed = fixture.catalogs[0].0;
    let binding = DocumentBinding {
        collection: signed.collection,
        document: DocumentId::from_bytes([48; 16]),
        generation: signed.generation,
        schema_digest: signed.schema_digest,
    };
    let store = fixture.open().expect("reopen with tight disk envelope");
    let result = store.commit_documents(
        TransactionId::new(50_000, [48; 16]),
        &[DocumentWrite {
            binding,
            observed: &CausalContext::new(),
            value: DocumentWriteValue::Tombstone,
        }],
    );
    assert!(matches!(
        result,
        Err(DocumentError::Storage(tb_storage::StorageError::Capacity(
            "SSD bytes and stage headroom"
        )))
    ));
    assert!(
        store
            .next_source_after(0)
            .expect("no source after disk refusal")
            .is_none()
    );
    drop(store);
    assert_eq!(fixture.visible().0, 1, "disk refusal wrote no D6 or final");
    fixture.config.storage.format = default_format;
    fixture.config.storage.max_disk_bytes = default_disk;
    let store = fixture.open().expect("restore disk envelope");
    let valid = store
        .commit_documents(
            TransactionId::new(50_000, [49; 16]),
            &[DocumentWrite {
                binding,
                observed: &CausalContext::new(),
                value: DocumentWriteValue::Tombstone,
            }],
        )
        .expect("valid source after disk refusal");
    assert_eq!(valid.receipt.commit_seq.get(), 3);
}

#[cfg(feature = "slow-tests")]
#[test]
fn crash_source_child_process() {
    if std::env::var_os(SOURCE_CRASH_CHILD_ENV).is_none() {
        return;
    }
    let directory = PathBuf::from(
        std::env::var_os(SOURCE_CRASH_DIRECTORY_ENV)
            .expect("parent supplies existing signed directory"),
    );
    let marker = PathBuf::from(
        std::env::var_os(SOURCE_CRASH_EXECUTED_ENV)
            .expect("parent supplies child execution marker"),
    );
    fs::write(marker, b"entered").expect("record executed source crash child");
    let fixture = Fixture::new_in(TempShard(directory), AuthorityVersion::V2, &[1]);
    let store = fixture.open().expect("child opens signed document shard");
    let signed = fixture.catalogs[0].0;
    let binding = DocumentBinding {
        collection: signed.collection,
        document: DocumentId::from_bytes([30; 16]),
        generation: signed.generation,
        schema_digest: signed.schema_digest,
    };
    store
        .commit_documents(
            TransactionId::new(50_000, [31; 16]),
            &[DocumentWrite {
                binding,
                observed: &CausalContext::new(),
                value: DocumentWriteValue::Tombstone,
            }],
        )
        .expect("storage crash hook should terminate before source returns");
    panic!("configured storage crash hook did not terminate child");
}

#[cfg(feature = "slow-tests")]
fn crash_source_case(prestage: bool) {
    let mut fixture = Fixture::new(AuthorityVersion::V2, &[1]);
    drop(fixture.open().expect("install signed C0 before child"));
    let signed = fixture.catalogs[0].0;
    let document = DocumentId::from_bytes([30; 16]);
    let binding = DocumentBinding {
        collection: signed.collection,
        document,
        generation: signed.generation,
        schema_digest: signed.schema_digest,
    };
    if prestage {
        assert_eq!(prestage_tombstone(&fixture, document), binding);
        assert_eq!(fixture.visible().0, 2);
    }
    let directory_guard = fixture.directory.take().expect("parent owns cleanup guard");
    let directory_path = directory_guard.0.clone();
    drop(fixture); // Release AcceptedAuthority's process-exclusive lock before child open.
    let child_marker = directory_path.with_extension("source-crash-child-entered");
    let mut command =
        Command::new(std::env::current_exe().expect("source integration test binary"));
    command
        .arg("--exact")
        .arg("document_slow::distributed_source::crash_source_child_process")
        .env(SOURCE_CRASH_CHILD_ENV, "1")
        .env(SOURCE_CRASH_DIRECTORY_ENV, &directory_path)
        .env(SOURCE_CRASH_EXECUTED_ENV, &child_marker)
        .env("TB_STORAGE_CRASH_POINT", "after_wal_sync_before_apply");
    let status = command.status().expect("run source crash child");
    assert_eq!(
        fs::read(&child_marker).expect("selected crash child executed"),
        b"entered"
    );
    fs::remove_file(&child_marker).expect("remove child execution marker");
    assert_eq!(status.code(), Some(86), "child must stop after WAL sync");

    let fixture = Fixture::new_in(directory_guard, AuthorityVersion::V2, &[1]);
    let (physical_sequence, rows) = fixture.visible();
    assert_eq!(
        physical_sequence,
        if prestage { 3 } else { 2 },
        "synced crash frame recovers at its physical sequence"
    );
    if prestage {
        assert_eq!(
            rows.iter()
                .filter(|row| row.key.first() == Some(&0xD2))
                .count(),
            1
        );
    } else {
        assert!(
            rows.iter()
                .all(|row| matches!(row.key.first().copied(), Some(0xC0 | 0xD6))),
            "post-stage crash publishes no D0/D1/D2/D3/D4 final rows"
        );
    }
    let store = fixture
        .open()
        .expect("replay child WAL and audit distributed shard");
    let source_before_retry = store
        .next_source_after(0)
        .expect("enumerate retained source");
    if prestage {
        assert!(
            source_before_retry.is_some(),
            "final frame recovered from WAL"
        );
    } else {
        assert!(
            source_before_retry.is_none(),
            "D6-only stage is not a source event"
        );
    }
    let committed = store
        .commit_documents(
            TransactionId::new(50_000, [31; 16]),
            &[DocumentWrite {
                binding,
                observed: &CausalContext::new(),
                value: DocumentWriteValue::Tombstone,
            }],
        )
        .expect("same caller token resolves after crash");
    assert_eq!(committed.event.sequence, 1);
    assert_eq!(committed.receipt.commit_seq.get(), 3);
    if let Some(recovered) = &source_before_retry {
        assert_eq!(recovered.metadata(), committed.metadata());
        assert_eq!(recovered.wal_frame(), committed.wal_frame());
        assert_eq!(recovered.event_digest, committed.event_digest);
    }
    drop(committed);
    drop(source_before_retry);
    drop(store);
    assert_eq!(fixture.visible().0, 3, "no duplicate stage or final event");
    let reopened = fixture.open().expect("fully recover after retry");
    assert!(
        reopened
            .next_source_after(1)
            .expect("resend cursor")
            .is_none()
    );
}

#[cfg(feature = "slow-tests")]
#[test]
fn crash_after_d6_sync_replans_only_missing_work_then_commits_once() {
    crash_source_case(false);
}

#[cfg(feature = "slow-tests")]
#[test]
fn crash_after_final_sync_retries_exact_retained_source_without_new_event() {
    crash_source_case(true);
}
