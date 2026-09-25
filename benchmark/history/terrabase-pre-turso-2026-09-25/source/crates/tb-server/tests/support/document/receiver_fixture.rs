// Shared public-API fixture for the Fast and Slow receiver integration scenarios.
pub(crate) use std::fs;
pub(crate) use std::path::PathBuf;
pub(crate) use std::sync::Arc;
pub(crate) use std::sync::atomic::{AtomicU64, Ordering};
pub(crate) use std::time::{SystemTime, UNIX_EPOCH};

pub(crate) use aws_lc_rs::signature::{Ed25519KeyPair, KeyPair};
pub(crate) use tb_core::{
    Attribution, BudgetConfig, CollectionId, DatabaseId, DocumentId, Key, MembershipEpoch,
    MemoryBudget, MemoryCategory, OriginId, ReplicaId, ShardId, TenantId, TransactionId, WorkerId,
};
pub(crate) use tb_document::{
    DistributedDocumentStore, DocumentApplyOutcome, DocumentError, DocumentStoreConfig,
    DocumentWrite, DocumentWriteValue, open_distributed,
};
pub(crate) use tb_format::{
    DocumentBinding, DocumentDot, DocumentEventId, DocumentFieldInput, DocumentKind,
    DocumentSibling, FieldType, FormatLimits, SchemaField, WalOperation, decode_causal_manifest,
    decode_state_pointer, encode_applied_key, encode_catalog_into, encode_change_into,
    encode_change_key, encode_document_into, encode_high_water_key, encode_high_water_value,
    encode_marker_key, encode_primary_key, encode_retry_key, encode_retry_value,
    encode_schema_into, encode_state_into, encode_wal_frame_into,
};
pub(crate) use tb_replication::{
    AcceptedAuthority, AuthorityCertificateScope, AuthorityMemberInput, AuthorityPayload,
    AuthoritySchemaInput, CausalContext, CollectionBinding, CursorLimits, CursorScope, EventId,
    LocalAuthorityScope, MetadataChange, MetadataHeader, PeerHelloScope, ReplicaReceiver,
    ReplicationLimits, WalRole, compute_event_digest, encode_metadata_into, verify_incoming,
};
#[cfg(feature = "slow-tests")]
pub(crate) use tb_replication::{AppendFailureKind, DurableReadKind};
#[cfg(feature = "slow-tests")]
pub(crate) use tb_storage::Mutation;
pub(crate) use tb_storage::{DistributedDocumentMode, Storage, StorageConfig};

pub(crate) static NEXT_DIRECTORY: AtomicU64 = AtomicU64::new(0);
pub(crate) const CLUSTER: [u8; 16] = [9; 16];
pub(crate) const TENANT: TenantId = TenantId::from_bytes([1; 16]);
pub(crate) const DATABASE: DatabaseId = DatabaseId::from_bytes([2; 16]);
pub(crate) const SHARD: ShardId = ShardId::new(1);
pub(crate) const COLLECTION: CollectionId = CollectionId::from_bytes([10; 16]);
pub(crate) const DOCUMENT: DocumentId = DocumentId::from_bytes([17; 16]);
pub(crate) const ORIGIN_A: OriginId = OriginId {
    replica_id: ReplicaId::from_bytes([3; 16]),
    incarnation: [4; 16],
};
pub(crate) const ORIGIN_B: OriginId = OriginId {
    replica_id: ReplicaId::from_bytes([5; 16]),
    incarnation: [6; 16],
};
pub(crate) const ORIGIN_C: OriginId = OriginId {
    replica_id: ReplicaId::from_bytes([7; 16]),
    incarnation: [8; 16],
};
pub(crate) const ORIGIN_D: OriginId = OriginId {
    replica_id: ReplicaId::from_bytes([9; 16]),
    incarnation: [10; 16],
};
pub(crate) const ORIGIN_E: OriginId = OriginId {
    replica_id: ReplicaId::from_bytes([11; 16]),
    incarnation: [12; 16],
};
pub(crate) const ORIGINS: [OriginId; 5] = [ORIGIN_A, ORIGIN_B, ORIGIN_C, ORIGIN_D, ORIGIN_E];
pub(crate) const CURSOR_LIMITS: CursorLimits = CursorLimits {
    max_pending: 32,
    max_sequence_distance: 32,
};

pub(crate) struct TempDirectory(PathBuf);

impl TempDirectory {
    pub(crate) fn new() -> Self {
        let nanos = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .expect("clock after epoch")
            .as_nanos();
        Self(std::env::temp_dir().join(format!(
            "terrabase-distributed-apply-{}-{nanos}-{}",
            std::process::id(),
            NEXT_DIRECTORY.fetch_add(1, Ordering::Relaxed)
        )))
    }
}

impl Drop for TempDirectory {
    fn drop(&mut self) {
        if self.0.exists() {
            fs::remove_dir_all(&self.0).expect("remove test shard");
        }
    }
}

pub(crate) fn leaf(origin: OriginId) -> &'static [u8] {
    if origin == ORIGIN_A {
        b"leaf-a"
    } else if origin == ORIGIN_B {
        b"leaf-b"
    } else if origin == ORIGIN_C {
        b"leaf-c"
    } else if origin == ORIGIN_D {
        b"leaf-d"
    } else if origin == ORIGIN_E {
        b"leaf-e"
    } else {
        panic!("unknown test origin")
    }
}

pub(crate) fn budget() -> MemoryBudget {
    MemoryBudget::new(BudgetConfig::new(256 * 1024 * 1024, 0).expect("test budget"))
}

pub(crate) fn replication_limits() -> ReplicationLimits {
    ReplicationLimits {
        max_origins: 16,
        max_changes: 32,
        max_siblings: 16,
        max_value_bytes: tb_core::MAX_VALUE_BYTES,
        max_payload_bytes: tb_core::MAX_WAL_GROUP_BYTES,
    }
}

pub(crate) fn schema_bytes() -> Vec<u8> {
    let fields = [SchemaField {
        field_id: 1,
        parent_field_id: 0,
        value_type: FieldType::Utf8,
        element_type: None,
        flags: 0,
        name: "value",
    }];
    let mut bytes = Vec::new();
    encode_schema_into(&mut bytes, &fields).expect("canonical test schema");
    bytes
}

pub(crate) struct EventPackage {
    pub(crate) event: DocumentEventId,
    pub(crate) metadata: Vec<u8>,
    pub(crate) wal_frame: Vec<u8>,
    pub(crate) digest: tb_replication::EventDigest,
    pub(crate) source_changes: Vec<(Vec<u8>, Vec<u8>)>,
}

impl EventPackage {
    pub(crate) fn from_bytes(
        fixture: &Fixture,
        event: DocumentEventId,
        metadata: Vec<u8>,
        wal_frame: Vec<u8>,
    ) -> Self {
        let digest = compute_event_digest(&metadata, &wal_frame).expect("canonical event digest");
        let proof = verify_incoming(
            &metadata,
            &wal_frame,
            digest,
            fixture.config.replication,
            &fixture.memory,
            Attribution {
                tenant: TENANT,
                worker: WorkerId(1),
                category: MemoryCategory::Replication,
            },
        )
        .expect("semantic test event");
        assert_eq!(
            proof.event().id().sequence(),
            event.sequence,
            "package event matches its proof"
        );
        let source_changes = proof
            .source_changes()
            .iter()
            .map(|change| (change.key().to_vec(), change.value().to_vec()))
            .collect();
        Self {
            event,
            metadata,
            wal_frame,
            digest,
            source_changes,
        }
    }

    pub(crate) fn from_commit(
        fixture: &Fixture,
        commit: &tb_document::DistributedDocumentCommit,
    ) -> Self {
        Self::from_bytes(
            fixture,
            commit.event,
            commit.metadata().to_vec(),
            commit.wal_frame().to_vec(),
        )
    }
}

pub(crate) struct Fixture {
    pub(crate) local_origin: OriginId,
    pub(crate) authority: Arc<AcceptedAuthority>,
    pub(crate) config: DocumentStoreConfig,
    pub(crate) memory: MemoryBudget,
    pub(crate) binding: CollectionBinding,
    pub(crate) _directory: TempDirectory,
}

impl Fixture {
    pub(crate) fn new(local_origin: OriginId) -> Self {
        Self::new_with_sibling_limit(local_origin, 16)
    }

    pub(crate) fn new_with_sibling_limit(local_origin: OriginId, max_siblings: usize) -> Self {
        Self::new_with_memory_budget(local_origin, max_siblings, budget())
    }

    pub(crate) fn new_with_memory_budget(
        local_origin: OriginId,
        max_siblings: usize,
        memory: MemoryBudget,
    ) -> Self {
        let directory = TempDirectory::new();
        let schema = schema_bytes();
        let mut catalog = Vec::new();
        let digest = encode_catalog_into(&mut catalog, COLLECTION, 1, 0, &schema)
            .expect("canonical test catalog");
        let binding = CollectionBinding {
            collection: COLLECTION,
            generation: 1,
            schema_digest: digest,
        };
        let members = [
            AuthorityMemberInput {
                origin: ORIGIN_A,
                leaf_der: leaf(ORIGIN_A),
                server_name: "a.test",
                failure_domain: "zone-a",
            },
            AuthorityMemberInput {
                origin: ORIGIN_B,
                leaf_der: leaf(ORIGIN_B),
                server_name: "b.test",
                failure_domain: "zone-b",
            },
            AuthorityMemberInput {
                origin: ORIGIN_C,
                leaf_der: leaf(ORIGIN_C),
                server_name: "c.test",
                failure_domain: "zone-c",
            },
            AuthorityMemberInput {
                origin: ORIGIN_D,
                leaf_der: leaf(ORIGIN_D),
                server_name: "d.test",
                failure_domain: "zone-d",
            },
            AuthorityMemberInput {
                origin: ORIGIN_E,
                leaf_der: leaf(ORIGIN_E),
                server_name: "e.test",
                failure_domain: "zone-e",
            },
        ];
        let schema_rows = [AuthoritySchemaInput {
            binding,
            catalog: &catalog,
        }];
        let active = [binding];
        let scope = AuthorityCertificateScope {
            cluster: CLUSTER,
            tenant: TENANT,
            database: DATABASE,
            shard: SHARD,
            epoch: MembershipEpoch::new(1),
        };
        let payload = AuthorityPayload::encode_v2(scope, &members, &schema_rows, &active)
            .expect("canonical test authority");
        let key = Ed25519KeyPair::from_seed_unchecked(&[7; 32]).expect("fixed test signing key");
        let public_key: [u8; 32] = key.public_key().as_ref().try_into().expect("key width");
        let signature: [u8; 64] = key
            .sign(&payload.signing_input().expect("authority signing bytes"))
            .as_ref()
            .try_into()
            .expect("signature width");
        let certificate = payload
            .finish(&signature, &public_key)
            .expect("signed test authority");
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
                    member: local_origin.replica_id,
                },
                &memory,
                Attribution {
                    tenant: TENANT,
                    worker: WorkerId(1),
                    category: MemoryCategory::Replication,
                },
            )
            .expect("accepted fixed signed authority"),
        );
        let configured_clock: Arc<dyn Fn() -> u64 + Send + Sync> = Arc::new(|| 1_000);
        let storage = StorageConfig::local(authority.view().documents_path(), TENANT, WorkerId(1))
            .with_clock(configured_clock);
        let mut replication = replication_limits();
        replication.max_siblings = max_siblings;
        let config = DocumentStoreConfig {
            storage,
            database: DATABASE,
            shard: SHARD,
            origin: local_origin,
            epoch: MembershipEpoch::new(1),
            configuration: authority.view().configuration(),
            replication,
        };
        Self {
            local_origin,
            authority,
            config,
            memory,
            binding,
            _directory: directory,
        }
    }

    pub(crate) fn mode(&self) -> DistributedDocumentMode {
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

    pub(crate) fn raw_storage(&self) -> Storage {
        Storage::open_distributed_document(
            self.config.storage.clone(),
            self.memory.clone(),
            self.mode(),
        )
        .expect("trusted test document storage open")
    }

    pub(crate) fn open(&self) -> DistributedDocumentStore {
        open_distributed(
            Arc::clone(&self.authority),
            self.config.clone(),
            self.memory.clone(),
        )
        .expect("open distributed document store")
    }

    pub(crate) fn receivers(&self) -> Vec<ReplicaReceiver> {
        let view = self.authority.view();
        ORIGINS
            .iter()
            .copied()
            .filter(|origin| *origin != self.local_origin)
            .map(|origin| {
                let scope = CursorScope {
                    database: DATABASE,
                    shard: SHARD,
                    origin,
                    epoch: view.epoch(),
                    configuration: view.configuration(),
                };
                let clock: Arc<dyn Fn() -> u64 + Send + Sync> = Arc::new(|| 1_000);
                ReplicaReceiver::open(
                    view,
                    StorageConfig::local(view.inbox_path(origin), TENANT, WorkerId(2))
                        .with_clock(clock),
                    self.memory.clone(),
                    scope,
                    CURSOR_LIMITS,
                )
                .expect("open fixed remote-origin inbox")
            })
            .collect()
    }

    pub(crate) fn ready_store(&self) -> DistributedDocumentStore {
        let store = self.open();
        store
            .install_receivers(self.receivers())
            .expect("install complete fixed inbox set");
        store
            .reconcile_inboxes()
            .expect("reconcile fresh inbox set");
        store
    }

    pub(crate) fn commit(
        &self,
        store: &DistributedDocumentStore,
        document: DocumentId,
        observed: &CausalContext,
        value: Option<&[u8]>,
        token: u8,
    ) -> EventPackage {
        let binding = DocumentBinding {
            collection: self.binding.collection,
            document,
            generation: self.binding.generation,
            schema_digest: self.binding.schema_digest,
        };
        let transaction_id = TransactionId::new(50_000, [token; 16]);
        let commit = match value {
            Some(bytes) => {
                let fields = [DocumentFieldInput {
                    field_id: 1,
                    value_type: FieldType::Utf8,
                    value: bytes,
                }];
                store
                    .commit_documents(
                        transaction_id,
                        &[DocumentWrite {
                            binding,
                            observed,
                            value: DocumentWriteValue::Live(&fields),
                        }],
                    )
                    .expect("source document commit")
            }
            None => store
                .commit_documents(
                    transaction_id,
                    &[DocumentWrite {
                        binding,
                        observed,
                        value: DocumentWriteValue::Tombstone,
                    }],
                )
                .expect("source tombstone commit"),
        };
        EventPackage::from_commit(self, &commit)
    }

    pub(crate) fn v1_event(
        &self,
        document: DocumentId,
        observed_entries: &[(OriginId, u64)],
        value: Option<&[u8]>,
        token: u8,
    ) -> EventPackage {
        self.v1_event_for_binding(self.binding, document, observed_entries, value, token)
    }

    pub(crate) fn v1_event_for_binding(
        &self,
        catalog_binding: CollectionBinding,
        document: DocumentId,
        observed_entries: &[(OriginId, u64)],
        value: Option<&[u8]>,
        token: u8,
    ) -> EventPackage {
        let observed = CausalContext::from_entries(
            observed_entries.iter().copied(),
            self.config.replication.max_origins,
        )
        .expect("canonical causal context");
        let sequence = 1;
        let event = DocumentEventId {
            database: DATABASE,
            shard: SHARD,
            origin: self.local_origin,
            sequence,
        };
        let event_id = EventId::new(DATABASE, SHARD, self.local_origin, sequence)
            .expect("valid test event ID");
        let transaction_id = TransactionId::new(50_000, [token; 16]);
        let document_binding = DocumentBinding {
            collection: catalog_binding.collection,
            document,
            generation: catalog_binding.generation,
            schema_digest: catalog_binding.schema_digest,
        };
        let (kind, document_bytes) = match value {
            Some(bytes) => {
                let mut encoded = Vec::new();
                encode_document_into(
                    &mut encoded,
                    document_binding,
                    &[DocumentFieldInput {
                        field_id: 1,
                        value_type: FieldType::Utf8,
                        value: bytes,
                    }],
                )
                .expect("valid test document");
                (DocumentKind::Live, encoded)
            }
            None => (DocumentKind::Tombstone, Vec::new()),
        };
        let roles = [
            WalRole::Marker,
            WalRole::SourceChange,
            WalRole::Primary,
            WalRole::Retry,
            WalRole::HighWater,
        ];
        let changes = [MetadataChange {
            collection: catalog_binding.collection,
            document,
            source_wal_ordinal: 1,
            kind,
            observed: observed.clone(),
        }];
        let mut metadata = Vec::new();
        encode_metadata_into(
            &mut metadata,
            MetadataHeader {
                id: event_id,
                epoch: self.config.epoch,
                configuration: self.config.configuration,
                transaction_id,
                record_format: 1,
                merge_policy: 1,
            },
            &[catalog_binding],
            &changes,
            &roles,
            self.config.replication,
        )
        .expect("canonical v1 test metadata");
        let mut change_value = Vec::new();
        encode_change_into(
            &mut change_value,
            event,
            document_binding,
            kind,
            &document_bytes,
        )
        .expect("canonical v1 D1");
        let source_key = encode_change_key(
            catalog_binding.collection,
            document,
            self.local_origin,
            sequence,
            0,
        )
        .expect("canonical D1 key");
        let mut context: Vec<_> = observed
            .entries()
            .map(|(origin, sequence)| DocumentDot { origin, sequence })
            .collect();
        context.push(DocumentDot {
            origin: self.local_origin,
            sequence,
        });
        context.sort_unstable();
        let mut state = Vec::new();
        encode_state_into(
            &mut state,
            catalog_binding.collection,
            document,
            &context,
            &[DocumentSibling {
                event,
                generation: catalog_binding.generation,
                schema_digest: catalog_binding.schema_digest,
                kind,
                document_bytes: &document_bytes,
            }],
        )
        .expect("canonical v1 inline primary state");
        let operations = [
            (
                encode_marker_key(self.local_origin, sequence)
                    .expect("marker key")
                    .to_vec(),
                metadata.clone(),
            ),
            (source_key.to_vec(), change_value),
            (
                encode_primary_key(catalog_binding.collection, document).to_vec(),
                state,
            ),
            (
                encode_retry_key(transaction_id).to_vec(),
                encode_retry_value(event).expect("retry value").to_vec(),
            ),
            (
                encode_high_water_key(self.local_origin)
                    .expect("high-water key")
                    .to_vec(),
                encode_high_water_value(sequence)
                    .expect("high-water value")
                    .to_vec(),
            ),
        ];
        let borrowed: Vec<_> = operations
            .iter()
            .map(|(key, value)| WalOperation::Put { key, value })
            .collect();
        let mut wal_frame = Vec::new();
        encode_wal_frame_into(
            &mut wal_frame,
            2,
            transaction_id,
            &borrowed,
            &FormatLimits::default(),
        )
        .expect("canonical v1 source frame");
        EventPackage::from_bytes(self, event, metadata, wal_frame)
    }

    pub(crate) fn receive(
        &self,
        store: &DistributedDocumentStore,
        package: &EventPackage,
    ) -> Result<DocumentApplyOutcome, DocumentError> {
        let view = self.authority.view();
        let member = view
            .certify_peer(
                PeerHelloScope {
                    cluster: CLUSTER,
                    tenant: TENANT,
                    database: DATABASE,
                    shard: SHARD,
                    origin: package.event.origin,
                    epoch: view.epoch(),
                    configuration: view.configuration(),
                },
                leaf(package.event.origin),
            )
            .expect("certified test member");
        let proof = verify_incoming(
            &package.metadata,
            &package.wal_frame,
            package.digest,
            self.config.replication,
            &self.memory,
            Attribution {
                tenant: TENANT,
                worker: WorkerId(2),
                category: MemoryCategory::Replication,
            },
        )
        .expect("verified incoming event bytes");
        let certified = view
            .certify_event(&member, &proof)
            .expect("certified event and signed catalog");
        store.receive_certified(certified)
    }

    pub(crate) fn exact_source_changes(&self, package: &EventPackage) {
        let storage = self.raw_storage();
        let snapshot = storage.snapshot();
        for (key_bytes, expected) in &package.source_changes {
            let key = Key::new(key_bytes.clone()).expect("verified source key");
            let actual = storage
                .get(snapshot, &key)
                .expect("read persisted source D1")
                .expect("receiver persisted source D1");
            assert_eq!(actual.bytes(), expected, "receiver copied exact D1 bytes");
        }
    }

    pub(crate) fn has_applied(&self, event: DocumentEventId) -> bool {
        let storage = self.raw_storage();
        let key = Key::new(encode_applied_key(event).expect("D5 key").to_vec()).expect("key");
        storage
            .get(storage.snapshot(), &key)
            .expect("read D5")
            .is_some()
    }

    pub(crate) fn has_primary(&self, document: DocumentId) -> bool {
        let storage = self.raw_storage();
        let key = Key::new(encode_primary_key(COLLECTION, document).to_vec()).expect("D0 key");
        storage
            .get(storage.snapshot(), &key)
            .expect("read D0")
            .is_some()
    }

    pub(crate) fn manifest(&self, document: DocumentId) -> tb_format::CausalManifest {
        let storage = self.raw_storage();
        let snapshot = storage.snapshot();
        let primary_bytes = encode_primary_key(COLLECTION, document);
        let primary_key = Key::new(primary_bytes.to_vec()).expect("D0 key");
        let pointer_value = storage
            .get(snapshot, &primary_key)
            .expect("read D0")
            .expect("D0 exists");
        let pointer =
            decode_state_pointer(&primary_bytes, pointer_value.bytes()).expect("valid D0 pointer");
        let manifest_key_bytes =
            tb_format::encode_causal_manifest_key(COLLECTION, document, pointer.manifest_sha256);
        let manifest_key = Key::new(manifest_key_bytes.to_vec()).expect("D6 key");
        let manifest_value = storage
            .get(snapshot, &manifest_key)
            .expect("read D6")
            .expect("D0 references D6");
        decode_causal_manifest(
            &manifest_key_bytes,
            manifest_value.bytes(),
            DATABASE,
            SHARD,
            self.config.replication.max_origins,
            self.config.replication.max_siblings,
        )
        .expect("valid receiver D6 manifest")
    }

    #[cfg(feature = "slow-tests")]
    pub(crate) fn seed_source_change(&self, package: &EventPackage) {
        let (key_bytes, value) = package.source_changes.first().expect("one test D1");
        let mutation = Mutation::Put {
            key: Key::new(key_bytes.clone()).expect("source D1 key"),
            value: value.clone(),
        };
        let storage = self.raw_storage();
        storage
            .commit_batch(
                storage.snapshot(),
                TransactionId::new(50_000, [250; 16]),
                &[mutation],
            )
            .expect("seed isolated D1-without-D5 corruption");
    }
}

pub(crate) fn context(entries: &[(OriginId, u64)]) -> CausalContext {
    CausalContext::from_entries(entries.iter().copied(), 16).expect("causal test context")
}

pub(crate) fn received_event_fixture(origin: OriginId) -> Fixture {
    Fixture::new(origin)
}
