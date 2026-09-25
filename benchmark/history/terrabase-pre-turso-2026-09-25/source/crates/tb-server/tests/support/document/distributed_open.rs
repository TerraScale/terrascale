use std::fs;
use std::path::PathBuf;
use std::sync::Arc;
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::{SystemTime, UNIX_EPOCH};

use aws_lc_rs::signature::{Ed25519KeyPair, KeyPair};
use tb_core::{
    Attribution, BudgetConfig, CollectionId, CommitSeq, DatabaseId, DocumentId, Key, KeyBound,
    KeyRange, MemoryBudget, MemoryCategory, OriginId, ReplicaId, ShardId, TenantId, TransactionId,
    WorkerId,
};
use tb_document::{DocumentError, DocumentStore, DocumentStoreConfig, open_distributed};
use tb_format::{
    ActiveSchema, FieldType, SchemaField, StatePointer, encode_active_key, encode_active_value,
    encode_catalog_into, encode_catalog_key, encode_schema_into, encode_state_pointer,
};
use tb_replication::{
    AcceptedAuthority, AuthorityCertificateScope, AuthorityMemberInput, AuthorityPayload,
    AuthoritySchemaInput, AuthorityVersion, CollectionBinding, ConfigurationId,
    LocalAuthorityScope, ReplicationLimits,
};
use tb_storage::{DistributedDocumentMode, Mutation, Storage, StorageConfig};

static NEXT_DIRECTORY: AtomicU64 = AtomicU64::new(0);
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

fn config(path: PathBuf) -> DocumentStoreConfig {
    DocumentStoreConfig {
        storage: StorageConfig::local(path, TenantId::from_bytes([1; 16]), WorkerId(1))
            .with_clock(Arc::new(|| 1_000)),
        database: DatabaseId::from_bytes([2; 16]),
        shard: ShardId::new(1),
        origin: OriginId {
            replica_id: ReplicaId::from_bytes([3; 16]),
            incarnation: [4; 16],
        },
        epoch: tb_core::MembershipEpoch::new(1),
        configuration: ConfigurationId::from_bytes([5; 32]).expect("test configuration"),
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
    MemoryBudget::new(BudgetConfig::new(64 * 1024 * 1024, 0).expect("test budget"))
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

struct Fixture {
    authority: Arc<AcceptedAuthority>,
    config: DocumentStoreConfig,
    memory: MemoryBudget,
    clock: Arc<AtomicU64>,
    catalogs: Vec<(CollectionBinding, Vec<u8>)>,
    directory: TempShard,
}

#[derive(Debug, Eq, PartialEq)]
struct VisibleRow {
    key: Vec<u8>,
    value: Vec<u8>,
}

impl Fixture {
    #[allow(
        clippy::too_many_lines,
        reason = "the signed fixture keeps one scope and catalog table consistent"
    )]
    fn new(version: AuthorityVersion, field_counts: &[usize]) -> Self {
        let directory = TempShard::new();
        let catalogs: Vec<_> = field_counts
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
            .collect();
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
            clock,
            catalogs,
            directory,
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

    fn signed_put(&self, index: usize) -> Mutation {
        let (binding, bytes) = &self.catalogs[index];
        Mutation::Put {
            key: Key::new(
                encode_catalog_key(binding.collection, binding.generation)
                    .expect("signed C0 key")
                    .to_vec(),
            )
            .expect("key"),
            value: bytes.clone(),
        }
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

    fn first_transaction_id(&self) -> TransactionId {
        let storage = self.raw_storage();
        storage
            .committed_cursor()
            .expect("WAL cursor")
            .next(
                CommitSeq::new(0),
                self.config.storage.format.max_frame_bytes,
            )
            .expect("first WAL frame")
            .expect("C0 install frame")
            .transaction_id
    }
}

#[test]
fn signed_v2_catalog_installs_reopens_without_new_commit() {
    let fixture = Fixture::new(AuthorityVersion::V2, &[1, 1]);
    assert_eq!(fixture.authority.view().version(), AuthorityVersion::V2);
    assert!(fixture.open().is_ok());
    let (sequence, rows) = fixture.visible();
    assert_eq!(sequence, 1);
    assert_eq!(rows.len(), 2);
    for (index, (binding, bytes)) in fixture.catalogs.iter().enumerate() {
        assert_eq!(
            rows[index],
            VisibleRow {
                key: encode_catalog_key(binding.collection, binding.generation)
                    .unwrap()
                    .to_vec(),
                value: bytes.clone(),
            }
        );
    }
    assert!(fixture.directory.0.join("documents.mode").is_file());
    fixture.clock.store(100_000, Ordering::SeqCst);
    assert!(
        fixture.open().is_ok(),
        "recovery does not need a fresh token"
    );
    assert_eq!(fixture.visible(), (sequence, rows));
}

#[test]
fn v1_and_wrong_signed_binding_refuse_before_document_marker() {
    let v1 = Fixture::new(AuthorityVersion::V1, &[1]);
    assert!(matches!(
        v1.open(),
        Err(DocumentError::Corrupt(
            "distributed documents require v2 authority"
        ))
    ));
    assert!(!v1.directory.0.join("documents.mode").exists());

    let fixture = Fixture::new(AuthorityVersion::V2, &[1]);
    let original = fixture.config.clone();
    let mut mismatches = Vec::with_capacity(8);
    let mut wrong = original.clone();
    wrong.storage.path = fixture.directory.0.join("wrong-documents");
    mismatches.push(wrong);
    let mut wrong = original.clone();
    wrong.storage.tenant = TenantId::from_bytes([44; 16]);
    mismatches.push(wrong);
    let mut wrong = original.clone();
    wrong.database = DatabaseId::from_bytes([44; 16]);
    mismatches.push(wrong);
    let mut wrong = original.clone();
    wrong.shard = ShardId::new(44);
    mismatches.push(wrong);
    let mut wrong = original.clone();
    wrong.epoch = tb_core::MembershipEpoch::new(44);
    mismatches.push(wrong);
    let mut wrong = original.clone();
    wrong.configuration = ConfigurationId::from_bytes([44; 32]).unwrap();
    mismatches.push(wrong);
    let mut wrong = original.clone();
    wrong.origin.replica_id = ReplicaId::from_bytes([44; 16]);
    mismatches.push(wrong);
    let mut wrong = original;
    wrong.origin.incarnation = [44; 16];
    mismatches.push(wrong);
    for config in mismatches {
        assert!(matches!(
            open_distributed(
                Arc::clone(&fixture.authority),
                config,
                fixture.memory.clone()
            ),
            Err(DocumentError::Corrupt(
                "distributed document binding mismatch"
            ))
        ));
        assert!(!fixture.directory.0.join("documents.mode").exists());
    }
}

#[test]
fn partial_signed_catalog_and_marker_only_restart_complete_exactly_once() {
    let marker_only = Fixture::new(AuthorityVersion::V2, &[1, 1]);
    drop(marker_only.raw_storage());
    assert_eq!(marker_only.visible().0, 0);
    assert!(marker_only.open().is_ok());
    assert_eq!(marker_only.visible().1.len(), 2);

    let partial = Fixture::new(AuthorityVersion::V2, &[1, 1]);
    partial.seed_distributed(&[partial.signed_put(0)]);
    let (before, rows) = partial.visible();
    assert_eq!(before, 1);
    assert_eq!(rows.len(), 1);
    assert!(partial.open().is_ok());
    let (after, rows) = partial.visible();
    assert_eq!(after, before + 1);
    assert_eq!(rows.len(), 2);
    assert!(partial.open().is_ok());
    assert_eq!(partial.visible().0, after);
}

fn raw_put(key: Vec<u8>, value: Vec<u8>) -> Mutation {
    Mutation::Put {
        key: Key::new(key).expect("raw test key"),
        value,
    }
}

#[test]
fn full_pre_audit_rejects_every_foreign_namespace_without_installing_missing_c0() {
    let mut cases = vec![
        (
            "extra C0",
            raw_put(
                encode_catalog_key(CollectionId::from_bytes([90; 16]), 1)
                    .unwrap()
                    .to_vec(),
                vec![1],
            ),
        ),
        (
            "C1",
            raw_put(
                encode_active_key(CollectionId::from_bytes([90; 16])).to_vec(),
                vec![1],
            ),
        ),
    ];
    for tag in 0xD0..=0xD6 {
        cases.push(("D namespace", raw_put(vec![tag, 1], vec![1])));
    }
    for tag in [0xE0, 0xEF, 0x01] {
        cases.push(("foreign raw namespace", raw_put(vec![tag, 1], vec![1])));
    }
    for (label, foreign) in cases {
        let mut fixture = Fixture::new(AuthorityVersion::V2, &[1, 1]);
        fixture.config.storage.max_scan_rows = 1;
        fixture.seed_distributed(&[fixture.signed_put(0), foreign]);
        let before = fixture.visible();
        assert!(
            fixture.open().is_err(),
            "{label} must prevent catalog readiness"
        );
        assert_eq!(fixture.visible(), before, "{label} must not append C0");
    }

    let fixture = Fixture::new(AuthorityVersion::V2, &[1, 1]);
    let mut altered = fixture.signed_put(0);
    let Mutation::Put { value, .. } = &mut altered else {
        unreachable!("signed fixture is a Put")
    };
    value[20] ^= 1;
    fixture.seed_distributed(&[altered]);
    let before = fixture.visible();
    assert!(fixture.open().is_err(), "altered signed C0 must fail");
    assert_eq!(fixture.visible(), before, "altered C0 must not append");
}

#[test]
fn two_maximum_legal_c0_rows_progress_with_one_row_scan_bytes_and_items() {
    let mut fixture = Fixture::new(AuthorityVersion::V2, &[1_024, 1_024]);
    let largest = fixture
        .catalogs
        .iter()
        .map(|(_, row)| row.len())
        .max()
        .unwrap();
    assert!(largest > 140_000, "exercise the 1,024-field schema ceiling");
    let one_row_bytes = largest + tb_format::CATALOG_KEY_LEN + 128;
    fixture.config.storage.max_scan_bytes = one_row_bytes;
    fixture.config.storage.max_scan_rows = 256;
    assert!(fixture.open().is_ok(), "one legal row must make progress");
    assert_eq!(fixture.visible().1.len(), 2);
    assert!(
        fixture.open().is_ok(),
        "pre-audit also pages after recovery"
    );

    let mut tight_items = Fixture::new(AuthorityVersion::V2, &[1]);
    tight_items.memory = MemoryBudget::new(
        BudgetConfig::new(64 * 1024 * 1024, 0)
            .unwrap()
            .with_category_item_limit(MemoryCategory::Query, 1)
            .with_category_item_limit(MemoryCategory::WriteBuffer, 1),
    );
    assert!(
        tight_items.open().is_ok(),
        "one-row scan and one-C0 batch must fit tight category item caps"
    );
    assert_eq!(tight_items.visible().1.len(), 1);

    let mut tight_bytes = Fixture::new(AuthorityVersion::V2, &[1]);
    tight_bytes.config.storage.format.max_value_bytes = 4_096;
    tight_bytes.config.storage.max_scan_bytes = 64 * 1024;
    tight_bytes.memory = MemoryBudget::new(BudgetConfig::new(512 * 1024, 0).unwrap());
    assert!(
        tight_bytes.open().is_ok(),
        "actual one-row buffer fits without claiming a 2-MiB frame"
    );
    assert_eq!(tight_bytes.visible().1.len(), 1);
}

#[test]
fn catalog_open_refuses_scan_frame_disk_and_admission_limits_without_readiness() {
    let mut too_small_scan = Fixture::new(AuthorityVersion::V2, &[1_024]);
    too_small_scan.seed_distributed(&[too_small_scan.signed_put(0)]);
    let before = too_small_scan.visible();
    too_small_scan.config.storage.max_scan_bytes =
        too_small_scan.catalogs[0].1.len() + tb_format::CATALOG_KEY_LEN + 127;
    assert!(
        too_small_scan.open().is_err(),
        "one C0 cannot fit scan page"
    );
    assert_eq!(too_small_scan.visible(), before);

    let mut too_small_frame = Fixture::new(AuthorityVersion::V2, &[1_024]);
    too_small_frame.config.storage.format.max_frame_bytes =
        too_small_frame.catalogs[0].1.len() + tb_format::CATALOG_KEY_LEN + 68;
    assert!(too_small_frame.open().is_err(), "one C0 exceeds WAL frame");
    assert_eq!(too_small_frame.visible(), (0, Vec::new()));

    let mut no_query_item = Fixture::new(AuthorityVersion::V2, &[1]);
    no_query_item.memory = MemoryBudget::new(
        BudgetConfig::new(64 * 1024 * 1024, 0)
            .unwrap()
            .with_category_item_limit(MemoryCategory::Query, 0),
    );
    assert!(no_query_item.open().is_err());
    assert_eq!(no_query_item.visible(), (0, Vec::new()));

    let mut no_query_bytes = Fixture::new(AuthorityVersion::V2, &[1]);
    no_query_bytes.memory = MemoryBudget::new(BudgetConfig::new(256 * 1024, 0).unwrap());
    no_query_bytes.config.storage.max_scan_bytes = 512 * 1024;
    assert!(no_query_bytes.open().is_err());
    assert_eq!(no_query_bytes.visible(), (0, Vec::new()));

    let mut disk_bound = Fixture::new(AuthorityVersion::V2, &[1_024; 16]);
    disk_bound.config.storage.format.max_frame_bytes = 1024 * 1024;
    disk_bound.config.storage.max_disk_bytes = 3 * 1024 * 1024;
    assert!(
        disk_bound.open().is_err(),
        "quota cannot hold all signed C0"
    );
    let (prefix, rows) = disk_bound.visible();
    assert!(prefix > 0 && !rows.is_empty() && rows.len() < 16);
    disk_bound.config.storage.max_disk_bytes = 16 * 1024 * 1024;
    assert!(
        disk_bound.open().is_ok(),
        "quota increase resumes signed prefix"
    );
    assert_eq!(disk_bound.visible().1.len(), 16);
}

#[test]
fn configured_clock_bounds_install_token_and_expired_attempt_never_becomes_ready() {
    let mut fixed = Fixture::new(AuthorityVersion::V2, &[1]);
    fixed.config.storage.max_retry_window_ms = 1;
    assert!(fixed.open().is_ok());
    let token = fixed.first_transaction_id();
    assert_eq!(token.expires_at_unix_ms(), 1_001);
    assert!(fixed.open().is_ok(), "completed C0 needs no new token");
    assert_eq!(fixed.visible().0, 1);

    let mut identical = Fixture::new(AuthorityVersion::V2, &[1]);
    identical.config.storage.max_retry_window_ms = 1;
    assert!(identical.open().is_ok());
    assert_eq!(identical.first_transaction_id(), token);
    let mut different_batch = Fixture::new(AuthorityVersion::V2, &[2]);
    different_batch.config.storage.max_retry_window_ms = 1;
    assert!(different_batch.open().is_ok());
    assert_ne!(
        different_batch.first_transaction_id().nonce(),
        token.nonce()
    );

    let default_window = Fixture::new(AuthorityVersion::V2, &[1]);
    assert!(default_window.open().is_ok());
    assert_eq!(
        default_window.first_transaction_id().expires_at_unix_ms(),
        61_000
    );

    let mut zero_window = Fixture::new(AuthorityVersion::V2, &[1]);
    zero_window.config.storage.max_retry_window_ms = 0;
    assert!(zero_window.open().is_err());
    assert_eq!(zero_window.visible(), (0, Vec::new()));

    let mut expiring = Fixture::new(AuthorityVersion::V2, &[1]);
    expiring.config.storage.max_retry_window_ms = 1;
    let tick = Arc::new(AtomicU64::new(1_000));
    let next = Arc::clone(&tick);
    expiring.config.storage = expiring
        .config
        .storage
        .clone()
        .with_clock(Arc::new(move || next.fetch_add(1, Ordering::SeqCst)));
    assert!(expiring.open().is_err(), "Storage rejects expired C0 token");
    assert_eq!(expiring.visible(), (0, Vec::new()));
}

fn seed(config: &DocumentStoreConfig, memory: &MemoryBudget, mutations: &[Mutation]) {
    let storage = Storage::open(config.storage.clone(), memory.clone()).expect("seed raw shard");
    storage
        .commit_batch(
            storage.snapshot(),
            TransactionId::new(50_000, [8; 16]),
            mutations,
        )
        .expect("commit raw fixture");
}

#[test]
fn ordinary_open_rejects_visible_d5_and_d6_on_later_scan_pages() {
    for tag in [0xD5, 0xD6] {
        let directory = TempShard::new();
        let mut config = config(directory.0.clone());
        config.storage.max_scan_rows = 1;
        let memory = budget();
        seed(
            &config,
            &memory,
            &[
                Mutation::Put {
                    key: Key::new(vec![0x01]).expect("key"),
                    value: vec![1],
                },
                Mutation::Put {
                    key: Key::new(vec![tag, 1]).expect("key"),
                    value: vec![2],
                },
            ],
        );
        assert!(matches!(
            DocumentStore::open(config, memory),
            Err(DocumentError::Corrupt(
                "distributed document namespace in local shard"
            ))
        ));
    }
}

#[test]
fn ordinary_open_rejects_publicly_decoded_v2_pointer() {
    let directory = TempShard::new();
    let config = config(directory.0.clone());
    let memory = budget();
    let (key, value) = encode_state_pointer(StatePointer {
        collection: CollectionId::from_bytes([6; 16]),
        document: DocumentId::from_bytes([7; 16]),
        manifest_sha256: [8; 32],
    });
    seed(
        &config,
        &memory,
        &[Mutation::Put {
            key: Key::new(key.to_vec()).expect("key"),
            value: value.to_vec(),
        }],
    );
    assert!(matches!(
        DocumentStore::open(config, memory),
        Err(DocumentError::Corrupt("v2 document pointer in local shard"))
    ));
}

#[test]
fn ordinary_open_retains_empty_local_shard() {
    let directory = TempShard::new();
    let store = DocumentStore::open(config(directory.0.clone()), budget());
    assert!(store.is_ok());
}

#[test]
fn ordinary_open_retains_valid_local_c0_only_and_c0_c1_fixtures() {
    let collection = CollectionId::from_bytes([42; 16]);
    let mut catalog = Vec::new();
    let digest = encode_catalog_into(&mut catalog, collection, 1, 0, &test_schema(1))
        .expect("local catalog");
    let catalog_key = encode_catalog_key(collection, 1).expect("catalog key");
    for active in [false, true] {
        let directory = TempShard::new();
        let config = config(directory.0.clone());
        let memory = budget();
        let mut mutations = vec![raw_put(catalog_key.to_vec(), catalog.clone())];
        if active {
            mutations.push(raw_put(
                encode_active_key(collection).to_vec(),
                encode_active_value(ActiveSchema {
                    generation: 1,
                    digest,
                })
                .expect("active value")
                .to_vec(),
            ));
        }
        seed(&config, &memory, &mutations);
        assert!(DocumentStore::open(config, memory).is_ok());
    }
}
