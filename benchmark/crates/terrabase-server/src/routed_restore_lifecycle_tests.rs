//! Local controlled-provider integration, not OCI deployment qualification.
use crate::admission::AdmissionRecord;
use crate::admitted_databases::AdmittedDatabaseRegistry;
use crate::native_backup_runtime::{AdmittedBackupEngine, NativeBackupInput, NativeBackupRuntime};
use std::sync::Arc;
use terrabase::backup::{
    BackupEncryption, BackupEnvelope, BackupEnvelopeParticipant, BackupPlan, PlacementLock,
    RestoreIdentity, RestorePlan,
    object_store::{ObjectDestination, Provider},
    run_backup,
    worker::{BackupEngineHandle, CustomerKmsBackupKeys, FleetLimits},
};
use terrabase::engine::EngineConfig;
use terrabase::identity::{DatabaseUlid, MemberUlid, ProjectUlid, TenantUlid};
use terrabase::member::{
    AcceptedDatabaseRole, AdmissionLimits, DatabaseId, DatabaseRole, DatabaseRoleAuthority,
    DatabaseScope, DatabaseSpec, Member,
};
use terrabase::regions::RegionCode;
use terrabase::shard_split::{FileShardIdentity, ShardRoute};
use terrabase::storage::DatabaseOwner;
use terrabase::types::{
    CollectionName, Document, DocumentId, DocumentValue, ExpectedVersion, LocalDurability,
    OperationId, TenantId,
};

struct Writer;

#[derive(Clone, Copy, Debug)]
enum KeyMode {
    Managed,
    Customer,
}

impl KeyMode {
    fn protocol(self) -> [u8; 5] {
        match self {
            Self::Managed => *b"TBKL\x01",
            Self::Customer => *b"TBCK\x01",
        }
    }
}

fn fixture_keys(
    scope: &DatabaseScope,
    purpose: terrabase::encryption::KeyPurpose,
    mode: KeyMode,
) -> Arc<dyn terrabase::encryption::DatabaseKeySource> {
    if matches!(mode, KeyMode::Managed) {
        return match purpose {
            terrabase::encryption::KeyPurpose::Data => {
                crate::test_keys::storage_keys(scope).into_values().next().unwrap()
            }
            terrabase::encryption::KeyPurpose::Backup => crate::test_keys::backup_keys(scope),
            _ => panic!("fixture only serves data and backup keys"),
        };
    }
    use terrabase::encryption::{
        CustomerKmsDatabaseKeys, CustomerKmsKeyDelivery, DatabaseKeyVersion, KeyScope,
        KeyVersionMetadata,
    };
    let delivery = crate::test_keys::key_delivery();
    let adapter = CustomerKmsKeyDelivery::new(
        delivery.socket.clone(),
        delivery.uid,
        delivery.gid,
        "aws-kms",
        "fixture-key",
        "sa-east-1",
        "https://kms.sa-east-1.amazonaws.com",
    )
    .unwrap();
    Arc::new(
        CustomerKmsDatabaseKeys::new(
            adapter,
            KeyVersionMetadata::new(
                KeyScope::new_with_project(
                    scope.environment().as_str(),
                    scope.tenant(),
                    scope.project(),
                    scope.database(),
                    purpose,
                )
                .unwrap(),
                DatabaseKeyVersion::from_text("01ARZ3NDEKTSV4RRFFQ69G5FB0").unwrap(),
            ),
            scope.region(),
        )
        .unwrap(),
    )
}

fn retained_fixture_keys(
    scope: &DatabaseScope,
    purpose: terrabase::encryption::KeyPurpose,
    mode: KeyMode,
    delivery: &crate::test_keys::KeyDeliveryFixture,
) -> Arc<dyn terrabase::encryption::DatabaseKeySource> {
    use terrabase::encryption::{
        CustomerKmsDatabaseKeys, CustomerKmsKeyDelivery, DatabaseKeyVersion, KeyScope,
        KeyVersionMetadata, OciVaultDatabaseKeys, OciVaultKeyDelivery,
    };
    let metadata = KeyVersionMetadata::new(
        KeyScope::new_with_project(
            scope.environment().as_str(),
            scope.tenant(),
            scope.project(),
            scope.database(),
            purpose,
        )
        .unwrap(),
        DatabaseKeyVersion::from_text("01ARZ3NDEKTSV4RRFFQ69G5FB0").unwrap(),
    );
    match mode {
        KeyMode::Managed => Arc::new(
            OciVaultDatabaseKeys::new(
                OciVaultKeyDelivery::new(delivery.socket.clone(), delivery.uid, delivery.gid)
                    .unwrap(),
                metadata,
                scope.region(),
            )
            .unwrap(),
        ),
        KeyMode::Customer => Arc::new(
            CustomerKmsDatabaseKeys::new(
                CustomerKmsKeyDelivery::new(
                    delivery.socket.clone(),
                    delivery.uid,
                    delivery.gid,
                    "aws-kms",
                    "fixture-key",
                    "sa-east-1",
                    "https://kms.sa-east-1.amazonaws.com",
                )
                .unwrap(),
                metadata,
                scope.region(),
            )
            .unwrap(),
        ),
    }
}

struct ObservedExecutor {
    inner: crate::production_data_runtime::ProductionRestoreExecutor,
    errors: Arc<std::sync::Mutex<Vec<String>>>,
    lose_result: Option<Arc<AdmittedDatabaseRegistry>>,
    executions: std::sync::atomic::AtomicUsize,
}

impl crate::native_recovery::RestoreExecutor for ObservedExecutor {
    fn reconcile<'a>(
        &'a self,
        request: &'a crate::native_recovery::NativeRestoreRequest,
    ) -> std::pin::Pin<Box<dyn std::future::Future<Output = Result<(), String>> + Send + 'a>> {
        crate::native_recovery::RestoreExecutor::reconcile(&self.inner, request)
    }

    fn execute<'a>(
        &'a self,
        request: &'a crate::native_recovery::NativeRestoreRequest,
    ) -> std::pin::Pin<
        Box<
            dyn std::future::Future<
                    Output = Result<crate::native_recovery::NativeRestoreOutcome, String>,
                > + Send
                + 'a,
        >,
    > {
        Box::pin(async move {
            self.executions.fetch_add(1, std::sync::atomic::Ordering::SeqCst);
            let result =
                crate::native_recovery::RestoreExecutor::execute(&self.inner, request).await;
            if result.is_ok()
                && let Some(registry) = &self.lose_result
            {
                // Fault injection: preserve the real executor's durable tree but
                // lose its result with the target paused before journal completion.
                registry.pause(&request.target_database_id, "lost-executor-result")?;
                return Err("injected loss after durable restore publication".into());
            }
            if let Err(error) = &result {
                self.errors.lock().unwrap().push(error.clone());
            }
            result
        })
    }
}
impl DatabaseRoleAuthority for Writer {
    fn accepted_role(&self, _: &DatabaseId) -> Option<AcceptedDatabaseRole> {
        Some(AcceptedDatabaseRole { epoch: 7, role: DatabaseRole::Writer })
    }
}

fn signed_control(value: &impl serde::Serialize, key_id: &str, seed: u8) -> Vec<u8> {
    use base64::Engine as _;
    let payload = serde_json::to_vec(value).unwrap();
    let key = aws_lc_rs::signature::Ed25519KeyPair::from_seed_unchecked(&[seed; 32]).unwrap();
    let mut message = b"terrabase-placement-transition-v1\0".to_vec();
    message.extend_from_slice(&payload);
    serde_json::to_vec(&terrabase_tbep::placement::transition::Signed {
        key_id: key_id.into(),
        payload: base64::engine::general_purpose::STANDARD.encode(payload),
        signature: base64::engine::general_purpose::STANDARD.encode(key.sign(&message)),
    })
    .unwrap()
}

fn sha256(bytes: &[u8]) -> String {
    hex::encode(aws_lc_rs::digest::digest(&aws_lc_rs::digest::SHA256, bytes).as_ref())
}

fn accepted_journal(
    root: &std::path::Path,
    records: &[AdmissionRecord],
    cluster: terrabase::identity::ClusterUlid,
    local: MemberUlid,
    peer: MemberUlid,
) -> Arc<terrabase_tbep::placement::PlacementJournal> {
    use aws_lc_rs::signature::KeyPair as _;
    use serde_json::json;
    use std::collections::{BTreeMap, BTreeSet};
    use terrabase_cluster::production::DatabaseRouteScope;
    use terrabase_tbep::placement::{PlacementBoundary, PlacementJournal};
    use terrabase_tbep::proto::terrabase::v1 as pb;
    let boundary = PlacementBoundary::new(
        "production",
        &cluster.to_text(),
        &local.to_text(),
        7,
        7,
        records.iter().map(|r| {
            (
                DatabaseRouteScope::try_new(&r.tenant_id, &r.project_id, &r.database_id).unwrap(),
                Some("GRU".into()),
                BTreeSet::from(["GRU".into()]),
            )
        }),
    )
    .unwrap();
    let journal =
        Arc::new(PlacementJournal::open(&root.join("placement.sqlite"), boundary).unwrap());
    let proposal = pb::PlacementProposal {
        environment: "production".into(),
        cluster: cluster.to_text(),
        generation: 7,
        epoch: 7,
        regions: vec![pb::PlacementRegionSeats {
            region: "GRU".into(),
            members: vec![local.to_text(), peer.to_text()],
        }],
        assignments: records
            .iter()
            .map(|r| pb::PlacementDatabaseAssignment {
                tenant: r.tenant_id.clone(),
                project: r.project_id.clone(),
                database: r.database_id.clone(),
                region: "GRU".into(),
                writer_member: local.to_text(),
                follower_member: peer.to_text(),
            })
            .collect(),
    };
    if journal.accepted().is_err() {
        journal
            .stage(
                "local-test-orchestrator",
                "placement",
                pb::PlacementStageRequest {
                    expected_revision: 0,
                    proposal: Some(proposal),
                    declarations: vec![],
                },
            )
            .unwrap();
    }
    // Sign the exact bytes durably staged by the public placement API, not
    // an independently reserialized approximation of the proposal.
    let proposal_bytes: Vec<u8> = rusqlite::Connection::open_with_flags(
        root.join("placement.sqlite"),
        rusqlite::OpenFlags::SQLITE_OPEN_READ_ONLY,
    )
    .unwrap()
    .query_row("SELECT proposal FROM proposals WHERE operation='placement'", [], |row| row.get(0))
    .unwrap();
    let key = |seed| {
        aws_lc_rs::signature::Ed25519KeyPair::from_seed_unchecked(&[seed; 32])
            .unwrap()
            .public_key()
            .as_ref()
            .to_vec()
    };
    let identities: Vec<_> = [local, peer]
        .iter()
        .map(|member| {
            terrabase_cluster::production::MemberWorkloadIdentity::try_new(
                "production",
                &cluster.to_text(),
                &member.to_text(),
            )
            .unwrap()
            .as_str()
            .to_owned()
        })
        .collect();
    journal
        .configure_transition_trust(
            terrabase_tbep::placement::transition::TransitionTrust::new(
                BTreeMap::from([("authority".into(), key(1))]),
                BTreeMap::from([("genesis".into(), key(2))]),
                identities.iter().map(|identity| (identity.clone(), key(3))).collect(),
            )
            .unwrap(),
        )
        .unwrap();
    // Canonical request field order is part of the existing signed protocol.
    #[derive(serde::Serialize)]
    struct Request {
        operation_id: String,
        scope_id: String,
        prior_epoch: u64,
        epoch: u64,
        generation: u64,
        members: Vec<Participant>,
    }
    #[derive(serde::Serialize)]
    struct Participant {
        identity: String,
        region: String,
        placement_digest: String,
    }
    let mut members: Vec<_> = identities
        .into_iter()
        .map(|identity| Participant {
            identity,
            region: "GRU".into(),
            placement_digest: sha256(&proposal_bytes),
        })
        .collect();
    members.sort_by(|a, b| a.identity.cmp(&b.identity));
    let request = Request {
        operation_id: "placement".into(),
        scope_id: format!("production/{cluster}"),
        prior_epoch: 0,
        epoch: 7,
        generation: 7,
        members,
    };
    let digest = sha256(&serde_json::to_vec(&request).unwrap());
    let genesis: serde_json::Value = serde_json::from_slice(&signed_control(
        &json!({"kind":"genesis_grant","grant_id":"local-test","request_digest":digest}),
        "genesis",
        2,
    ))
    .unwrap();
    let proof = json!({"schema_version":1,"kind":"accepted_epoch","request":request,"request_digest":digest,"authority_epoch":7,"journal_revision":1,"genesis_grant_id":"local-test","receipt_digests":[],"drain_receipts":[],"prior_generation":0,"previous_proof_digest":"","lease_id":"local-test-lease","genesis_grant":genesis});
    journal
        .accept(
            "placement",
            &pb::PlacementAcceptRequest {
                proposal_revision: 1,
                proof: signed_control(&proof, "authority", 1),
            },
        )
        .unwrap();
    journal
}

async fn production_owner(
    registry: Arc<AdmittedDatabaseRegistry>,
    member: Member,
    records: &[AdmissionRecord],
    root: &std::path::Path,
    keys: &crate::test_keys::KeyDeliveryFixture,
    mode: KeyMode,
    mismatch: bool,
) -> (
    Arc<crate::production_data_runtime::ProductionAdmissionRuntime>,
    Arc<terrabase_tbep::placement::PlacementJournal>,
    terrabase_cluster::production::ProductionQuicEndpoint,
) {
    production_owner_with_identity(
        registry,
        member,
        records,
        root,
        keys,
        mode,
        mismatch,
        terrabase::identity::ClusterUlid::generate(),
        MemberUlid::generate(),
    )
    .await
}

#[allow(clippy::too_many_arguments)]
async fn production_owner_with_identity(
    registry: Arc<AdmittedDatabaseRegistry>,
    member: Member,
    records: &[AdmissionRecord],
    root: &std::path::Path,
    keys: &crate::test_keys::KeyDeliveryFixture,
    mode: KeyMode,
    mismatch: bool,
    cluster: terrabase::identity::ClusterUlid,
    peer: MemberUlid,
) -> (
    Arc<crate::production_data_runtime::ProductionAdmissionRuntime>,
    Arc<terrabase_tbep::placement::PlacementJournal>,
    terrabase_cluster::production::ProductionQuicEndpoint,
) {
    use std::collections::BTreeSet;
    use terrabase_cluster::production::{
        DatabaseRouteScope, DatabaseRoutingCatalog, ProductionMemberRuntime, ProductionPlacement,
        ProductionRegionSeats,
    };
    let local = records[0].member_id.parse().unwrap();
    let placement = ProductionPlacement::try_new(
        local,
        7,
        7,
        [ProductionRegionSeats::try_new(RegionCode::GRU, [local, peer]).unwrap()],
    )
    .unwrap();
    let (transport, remote) =
        crate::production::tests::transport_pair_in_cluster(&placement, cluster);
    let journal = accepted_journal(root, records, cluster, local, peer);
    let routes = DatabaseRoutingCatalog::try_new(records.iter().map(|r| {
        DatabaseRouteScope::try_new(&r.tenant_id, &r.project_id, &r.database_id).unwrap()
    }))
    .unwrap();
    let runtime = Arc::new(
        ProductionMemberRuntime::try_new(placement, routes, member.clone(), transport).unwrap(),
    );
    let s = scope(&records[0]);
    let mut production = crate::auth_runtime::tests::admitted_fixture::production_config(
        (s.tenant(), s.project(), s.database()),
        cluster,
        local,
        peer,
        root,
        keys,
    );
    production.placement.generation = 7;
    production.placement.epoch = 7;
    let template = production.databases[0].clone();
    use std::os::unix::fs::PermissionsExt as _;
    let access = root.join("object-access.fixture");
    let secret = root.join("object-secret.fixture");
    for path in [&access, &secret] {
        std::fs::write(path, "local-controlled-provider-fixture").unwrap();
        std::fs::set_permissions(path, std::fs::Permissions::from_mode(0o600)).unwrap();
    }
    production.databases = records
        .iter()
        .map(|r| {
            let s = scope(r);
            let mut database = template.clone();
            database.tenant = s.tenant();
            database.project = s.project();
            database.database = s.database();
            database.location = root.join(&r.database_id);
            database.policy_journal = root.join(format!("policy-{}.sqlite", r.database_id));
            database.backup_root = root.join(format!("backup-{}", r.database_id));
            database.encryption_provider = match mode {
                KeyMode::Managed => terrabase::encryption::KeyProviderIdentity::OciVault,
                KeyMode::Customer => terrabase::encryption::KeyProviderIdentity::CustomerKms,
            };
            database.byok = (matches!(mode, KeyMode::Customer) != mismatch).then(|| {
                crate::config::ProductionByokConfig {
                    backup_endpoint: "https://s3.sa-east-1.amazonaws.com".into(),
                    backup_bucket: "routed-fixture".into(),
                    backup_region: "sa-east-1".into(),
                    backup_role_arn: "arn:aws:iam::123456789012:role/fixture".into(),
                    backup_external_id: None,
                    key_provider: "aws-kms".into(),
                    key_id: "fixture-key".into(),
                    key_region: "sa-east-1".into(),
                    key_endpoint: Some("https://kms.sa-east-1.amazonaws.com".into()),
                }
            });
            database.backup = Some(crate::config::ProductionBackupConfig {
                policy_revision: "local-routed-policy".into(),
                retention_days: 1,
                historical_destinations: vec![],
                destinations: vec![crate::config::ProductionBackupDestination {
                    provider: Provider::Oci,
                    endpoint: "https://fixture.compat.objectstorage.sa-saopaulo-1.oraclecloud.com"
                        .into(),
                    signing_region: "sa-saopaulo-1".into(),
                    bucket: "routed-fixture".into(),
                    verified_geography: "BR".into(),
                    permitted_geographies: vec!["BR".into()],
                    placement_revision: "local-routed-policy".into(),
                    access_key_file: access.clone(),
                    secret_key_file: secret.clone(),
                    max_artifact_bytes: 4 * 1024 * 1024,
                    max_point_bytes: 16 * 1024 * 1024,
                }],
            });
            database
        })
        .collect();
    let admission = Arc::new(
        crate::production_data_runtime::ProductionAdmissionRuntime::new(
            runtime,
            member,
            production,
            journal.clone(),
            registry,
            Arc::new(crate::replication_runtime::ReplicationProgress::default()),
            8,
            1,
            64 * 1024 * 1024,
            Arc::new(crate::production::tests::NoControlFrames),
        )
        .unwrap(),
    );
    let usage = crate::usage_admission::UsageAdmissionJournal::open(
        root,
        "production".into(),
        local.to_text(),
        "GRU".into(),
        journal.clone(),
    )
    .unwrap();
    for r in records {
        if mismatch {
            assert!(
                admission.admission_host().admit(r).is_err(),
                "mismatched provider/declaration must deny {mode:?}"
            );
            continue;
        }
        admission.admission_host().admit(r).unwrap();
        usage
            .admit(crate::usage_admission::UsageRegistration {
                schema_version: 1,
                environment: "production".into(),
                tenant_id: r.tenant_id.clone(),
                project_id: r.project_id.clone(),
                database_id: r.database_id.clone(),
                member_id: local.to_text(),
                region: "GRU".into(),
                selected_regions: BTreeSet::from(["GRU".to_owned()]).into_iter().collect(),
                residency: "GRU".into(),
                partition: "local-test-partition".into(),
                stream_epoch: 7,
                placement_proof_digest: journal.accepted().unwrap().proof_digest,
                operation_id: format!("usage-{}", r.database_id),
            })
            .unwrap();
    }
    if mismatch {
        assert!(
            keys.protocols.lock().unwrap().is_empty(),
            "mismatch must deny before key delivery"
        );
    } else {
        let protocols = keys.protocols.lock().unwrap().clone();
        assert!(!protocols.is_empty(), "actual admission must request a provider key");
        assert!(
            protocols.iter().all(|protocol| *protocol == mode.protocol()),
            "{mode:?}: {protocols:?}"
        );
        admission.reconcile().await.unwrap();
    }
    (admission, journal, remote)
}

fn scope(record: &AdmissionRecord) -> DatabaseScope {
    DatabaseScope::new(
        &record.environment,
        record.tenant_id.parse().unwrap(),
        record.project_id.parse().unwrap(),
        record.database_id.parse().unwrap(),
        RegionCode::GRU,
    )
    .unwrap()
}

fn config(record: &AdmissionRecord, location: &std::path::Path, mode: KeyMode) -> EngineConfig {
    let scope = scope(record);
    config_with_keys(
        record,
        location,
        fixture_keys(&scope, terrabase::encryption::KeyPurpose::Data, mode),
    )
}

fn retained_config(
    record: &AdmissionRecord,
    location: &std::path::Path,
    mode: KeyMode,
    delivery: &crate::test_keys::KeyDeliveryFixture,
) -> EngineConfig {
    let scope = scope(record);
    config_with_keys(
        record,
        location,
        retained_fixture_keys(&scope, terrabase::encryption::KeyPurpose::Data, mode, delivery),
    )
}

fn config_with_keys(
    record: &AdmissionRecord,
    location: &std::path::Path,
    keys: Arc<dyn terrabase::encryption::DatabaseKeySource>,
) -> EngineConfig {
    let scope = scope(record);
    EngineConfig::durable(location)
        .unwrap()
        .with_owner(
            DatabaseOwner::try_new(
                "production",
                scope.tenant(),
                scope.project(),
                scope.database(),
                scope.region(),
            )
            .unwrap(),
        )
        .with_storage_keys(keys)
}

fn put(engine: &terrabase::Engine, id: &str, value: i64) {
    let tenant = TenantId::try_new(&engine.database_owner().tenant().to_text()).unwrap();
    let scope = engine.scope_for(&tenant);
    let mut tx = engine
        .begin(
            &scope,
            engine.read_snapshot(&scope).unwrap(),
            OperationId::try_new(&format!("put-{id}-{value}")).unwrap(),
        )
        .unwrap();
    tx.put(
        Document::new(
            DocumentId::try_new(id).unwrap(),
            CollectionName::try_new("items").unwrap(),
            DocumentValue::Integer(value),
        ),
        ExpectedVersion::Any,
    )
    .unwrap();
    let outcome = tx.commit(LocalDurability::Synced);
    assert!(
        matches!(outcome, terrabase::CommitOutcome::Committed(_)),
        "put {id}={value}: {outcome:?}"
    );
}

fn identity(
    envelope: &BackupEnvelope,
    target: &AdmissionRecord,
    operation: &str,
) -> RestoreIdentity {
    RestoreIdentity {
        operation_id: operation.into(),
        source_database_id: envelope.database_id().into(),
        target_database_id: target.database_id.clone(),
        provider: "oci".into(),
        selection: envelope.digest().into(),
        expected_manifest_digest: envelope.digest().into(),
        expected_journal_boundary: envelope
            .participants()
            .iter()
            .map(|p| p.boundary)
            .max()
            .unwrap(),
        restore_epoch: 7,
    }
}

/// Only setup uses a manually assembled authenticated envelope. The source
/// subsequently enters production Member admission; no cut is fabricated.
fn routed_fixture(
    mode: KeyMode,
) -> (
    Arc<AdmittedDatabaseRegistry>,
    Member,
    Vec<AdmissionRecord>,
    std::path::PathBuf,
    crate::test_keys::KeyDeliveryFixture,
) {
    let root = std::env::temp_dir().join(format!("routed-native-{}", DatabaseUlid::generate()));
    std::fs::create_dir_all(&root).unwrap();
    use std::os::unix::fs::PermissionsExt as _;
    std::fs::set_permissions(&root, std::fs::Permissions::from_mode(0o700)).unwrap();
    let tenant = TenantUlid::generate();
    let project = ProjectUlid::generate();
    let local = MemberUlid::generate();
    let records: Vec<_> = (0..3)
        .map(|_| AdmissionRecord {
            created_at: 1,
            environment: "production".into(),
            tenant_id: tenant.to_text(),
            project_id: project.to_text(),
            database_id: DatabaseUlid::generate().to_text(),
            parent_database_id: None,
            key_version: "01ARZ3NDEKTSV4RRFFQ69G5FB0".into(),
            name: "routed-fixture".into(),
            regions: vec!["GRU".into()],
            residency: "GRU".into(),
            operation_id: DatabaseUlid::generate().to_text(),
            region: "GRU".into(),
            member_id: local.to_text(),
        })
        .collect();
    let seed = &records[2];
    let keys = fixture_keys(&scope(seed), terrabase::encryption::KeyPurpose::Backup, mode);
    let lease = keys.current_lease().unwrap();
    // Canonical v1 tuple encoding of ("items", "m"), the fixture's midpoint.
    let split = b"\x01items\0\0m\0\0".to_vec();
    let incarnation = u128::from_be_bytes(scope(seed).database().as_bytes());
    let mut participants = Vec::new();
    let mut archives = Vec::new();
    for (shard, id, lower, upper) in
        [(1, "a", None, Some(split.clone())), (2, "z", Some(split), None)]
    {
        let engine =
            terrabase::Engine::open(config(seed, &root.join(format!("seed-{shard}")), mode))
                .unwrap();
        engine.activate_origin_epoch(1).unwrap();
        put(&engine, id, shard as i64);
        let archive = root.join(format!("seed-archive-{shard}"));
        let encryption =
            BackupEncryption::from_database_key_lease(&lease, RegionCode::GRU, 0).unwrap();
        let manifest =
            run_backup(&engine, BackupPlan::full(encryption), &archive).unwrap().manifest().clone();
        let participant_id = format!("shard-{shard:016x}");
        archives.push((participant_id.clone(), archive));
        participants.push(BackupEnvelopeParticipant {
            participant_id,
            database_id: seed.database_id.clone(),
            database_incarnation: incarnation,
            route_generation: 3,
            boundary: manifest.boundary_sequence().get(),
            manifest,
            file_identity: FileShardIdentity {
                database_incarnation: incarnation,
                shard_id: shard,
                region: RegionCode::GRU,
                origin_lane_incarnation: u128::from(shard),
            },
            is_root: shard == 1,
            routes: vec![ShardRoute::new(shard, lower, upper).unwrap()],
        });
    }
    let envelope =
        BackupEnvelope::assemble(seed.database_id.clone(), incarnation, 3, participants).unwrap();
    let source_config = config(&records[0], &root.join(&records[0].database_id), mode);
    let source = terrabase::Engine::open(source_config.clone()).unwrap();
    let tenant = TenantId::try_new(&records[0].tenant_id).unwrap();
    source.lifecycle_pause(&source.scope_for(&tenant), "fixture-pause").unwrap();
    drop(source);
    RestorePlan::restore_envelope_encrypted_with_config(
        &envelope,
        &archives,
        &source_config,
        lease.as_ref(),
        &identity(&envelope, &records[0], "fixture-install"),
    )
    .unwrap();
    let source = terrabase::Engine::open(source_config).unwrap();
    source.lifecycle_resume(&source.scope_for(&tenant), "fixture-resume").unwrap();
    drop(source);
    let records = records[..2].to_vec();
    let specs: Vec<_> = records
        .iter()
        .map(|record| DatabaseSpec::scoped(scope(record), &record.database_id))
        .collect();
    let delivery = crate::test_keys::key_delivery();
    let keys = specs
        .iter()
        .map(|spec| {
            let scope = spec.scope().unwrap();
            (
                DatabaseId::from(scope.database()),
                retained_fixture_keys(
                    scope,
                    terrabase::encryption::KeyPurpose::Data,
                    mode,
                    &delivery,
                ),
            )
        })
        .collect();
    let member = Member::open_production(&root, specs, AdmissionLimits::new(16, 8), keys).unwrap();
    let registry = Arc::new(AdmittedDatabaseRegistry::new(Arc::new(|| true)));
    for record in &records {
        let id = DatabaseId::try_new(&record.database_id).unwrap();
        member.accept_database_role(&id, &Writer).unwrap();
        registry
            .publish(
                record.clone(),
                member.acquire(&id).unwrap(),
                root.join(&record.database_id),
                1,
            )
            .unwrap();
    }
    registry
        .activate(&records.iter().map(|record| record.database_id.clone()).collect::<Vec<_>>())
        .unwrap();
    (registry, member, records, root, delivery)
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn routed_native_capture_restores_all_admitted_files() {
    native_capture_restores_all_admitted_files(false).await;
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn routed_paused_native_backup_recomposes_after_fresh_member() {
    native_capture_restores_all_admitted_files(true).await;
}

async fn native_capture_restores_all_admitted_files(restart_paused: bool) {
    let mode = KeyMode::Managed;
    let (registry, member, records, root, delivery) = routed_fixture(mode);
    let source = registry.resolve(&records[0].database_id).unwrap().unwrap();
    assert_eq!(
        source.host.engine_host().admitted_shard_read_cut().unwrap().unwrap().participants.len(),
        2
    );
    let runtime = NativeBackupRuntime::new(
        FleetLimits {
            streams: 2,
            staging_bytes_per_stream: 32 * 1024 * 1024,
            inventory_bytes_per_stream: 32 * 1024 * 1024,
            points_per_stream: 8,
        },
        [NativeBackupInput {
            engine: Arc::new(AdmittedBackupEngine::new(source.host.clone())),
            keys: Arc::new(
                CustomerKmsBackupKeys::new(retained_fixture_keys(
                    &scope(&records[0]),
                    terrabase::encryption::KeyPurpose::Backup,
                    mode,
                    &delivery,
                ))
                .unwrap(),
            ),
            destinations: vec![(
                Provider::Oci,
                Arc::new(ObjectDestination::for_test(
                    Provider::Oci,
                    Arc::new(object_store::memory::InMemory::new()),
                    4 * 1024 * 1024,
                    16 * 1024 * 1024,
                )),
            )],
            historical_destinations: vec![],
            policy_revision: "routed-local-test".into(),
            retention_days: 1,
            staging: root.join("native-staging"),
            placement: PlacementLock::explicit([RegionCode::GRU]).unwrap(),
        }],
    )
    .unwrap();
    drop(source);
    let capture_errors = runtime.tick().await.unwrap();
    assert!(capture_errors.is_empty(), "{capture_errors:?}");
    let envelope = runtime
        .fleet()
        .lock()
        .unwrap()
        .retained_envelopes(&records[0].database_id)
        .unwrap()
        .pop()
        .unwrap();
    assert_eq!(envelope.participants().len(), 2);
    assert_eq!(envelope.logical_documents(), 2);
    registry.pause(&records[1].database_id, "restore-pause").unwrap();
    let reservation = registry
        .reserve_restore(&records[1].database_id)
        .unwrap()
        .rollback_on_drop(member.clone(), registry.clone());
    member.close_database(&DatabaseId::try_new(&records[1].database_id).unwrap()).unwrap();
    let receipt = runtime
        .restore_logical_from_provider(
            &records[0].database_id,
            Provider::Oci,
            envelope.digest(),
            &retained_config(&records[1], &root.join(&records[1].database_id), mode, &delivery),
            &PlacementLock::explicit([RegionCode::GRU]).unwrap(),
            &identity(&envelope, &records[1], "native-restore"),
        )
        .await
        .unwrap();
    assert_eq!(receipt.restored_documents, 2);
    let lease = reservation.reacquire(&member).unwrap();
    assert!(lease.admitted_shard_read_cut().is_err());
    assert!(
        lease
            .verify_published_restore(&identity(&envelope, &records[1], "native-restore"))
            .unwrap()
            .is_some()
    );
    assert!(lease.admitted_shard_read_cut().is_err(), "verification must not unpause");
    if restart_paused {
        let tenant = TenantId::try_new(&records[1].tenant_id).unwrap();
        assert!(lease.engine().lifecycle_state(&lease.engine().scope_for(&tenant)).unwrap().paused);
        reservation.complete(&registry, lease, 1).unwrap();
        registry.pause(&records[0].database_id, "restart-source-pause").unwrap();
        let cluster = terrabase::identity::ClusterUlid::generate();
        let peer = MemberUlid::generate();
        let (admission, journal, remote) = production_owner_with_identity(
            registry.clone(),
            member.clone(),
            &records,
            &root,
            &delivery,
            mode,
            false,
            cluster,
            peer,
        )
        .await;
        let proof = journal.accepted().unwrap().proof_digest;
        runtime.drain().unwrap();
        admission.lifecycle().drain().await.unwrap();
        drop((runtime, admission, journal, remote, registry));
        member.close(std::time::Duration::from_secs(2)).unwrap();
        drop(member);

        let specs: Vec<_> = records
            .iter()
            .map(|record| DatabaseSpec::scoped(scope(record), &record.database_id))
            .collect();
        let keys = specs
            .iter()
            .map(|spec| {
                let scope = spec.scope().unwrap();
                (
                    DatabaseId::from(scope.database()),
                    retained_fixture_keys(
                        scope,
                        terrabase::encryption::KeyPurpose::Data,
                        mode,
                        &delivery,
                    ),
                )
            })
            .collect();
        let member =
            Member::open_production(&root, specs, AdmissionLimits::new(16, 8), keys).unwrap();
        let registry = Arc::new(AdmittedDatabaseRegistry::new(Arc::new(|| true)));
        for record in &records {
            let id = DatabaseId::try_new(&record.database_id).unwrap();
            member.accept_database_role(&id, &Writer).unwrap();
            registry
                .publish_retained(
                    record.clone(),
                    member.acquire(&id).unwrap(),
                    root.join(&record.database_id),
                    1,
                )
                .unwrap();
        }
        let (admission, journal, remote) = production_owner_with_identity(
            registry.clone(),
            member.clone(),
            &records,
            &root,
            &delivery,
            mode,
            false,
            cluster,
            peer,
        )
        .await;
        assert_eq!(journal.accepted().unwrap().proof_digest, proof);
        let backups = admission
            .compose_native_backup(FleetLimits {
                streams: 2,
                staging_bytes_per_stream: 32 * 1024 * 1024,
                inventory_bytes_per_stream: 32 * 1024 * 1024,
                points_per_stream: 8,
            })
            .unwrap();
        for record in &records {
            assert!(backups.fleet().lock().unwrap().health(&record.database_id).is_some());
            assert_eq!(
                backups.capture(&record.database_id, "paused-capture").await.unwrap_err(),
                crate::native_backup_runtime::NativeBackupRuntimeError::Fleet(
                    terrabase::backup::worker::FleetError::Configuration
                )
            );
            assert!(registry.resolve(&record.database_id).unwrap().is_none());
        }
        let target = registry
            .retained_snapshot()
            .unwrap()
            .into_iter()
            .find(|entry| entry.database.record.database_id == records[1].database_id)
            .unwrap();
        let host = target.database.host.engine_host();
        assert!(host.admitted_shard_read_cut().is_err());
        assert!(
            host.verify_published_restore(&identity(&envelope, &records[1], "native-restore"))
                .unwrap()
                .is_some()
        );
        assert!(host.admitted_shard_read_cut().is_err());
        drop(target);
        backups.drain().unwrap();
        admission.lifecycle().drain().await.unwrap();
        drop((backups, admission, journal, remote, registry));
        member.close(std::time::Duration::from_secs(2)).unwrap();
        std::fs::remove_dir_all(root).unwrap();
        return;
    }
    let tenant = TenantId::try_new(&records[1].tenant_id).unwrap();
    lease.engine().lifecycle_resume(&lease.engine().scope_for(&tenant), "native-resume").unwrap();
    assert_eq!(lease.admitted_shard_read_cut().unwrap().participants.len(), 2);
    let query = lease
        .engine()
        .queries()
        .compile(terrabase::QueryInput::sql(
            terrabase::SqlQueryText::try_new("SELECT _id FROM items ORDER BY _id").unwrap(),
        ))
        .unwrap();
    let cut = lease.admitted_shard_read_cut().unwrap();
    let rows = lease.engine().queries().execute_admitted(&query, &cut).unwrap();
    assert_eq!(
        rows.rows().iter().map(|row| row.document().id().as_str()).collect::<Vec<_>>(),
        ["a", "z"]
    );
    drop((rows, cut));
    let collection = CollectionName::try_new("items").unwrap();
    for (id, expected) in [("a", 1), ("z", 2)] {
        let engine =
            lease.document_participant(&collection, &[DocumentId::try_new(id).unwrap()]).unwrap();
        let tenant = TenantId::try_new(&records[1].tenant_id).unwrap();
        let scope = engine.scope_for(&tenant);
        let docs = engine
            .snapshot_documents_in_collection(
                &scope,
                &engine.read_snapshot(&scope).unwrap(),
                &collection,
            )
            .unwrap();
        assert_eq!(docs.len(), 1);
        assert_eq!(docs[0].id().as_str(), id);
        assert_eq!(docs[0].value(), &DocumentValue::Integer(expected));
    }
    reservation.complete(&registry, lease, 1).unwrap();
    runtime.drain().unwrap();
    drop((runtime, registry));
    member.close(std::time::Duration::from_secs(2)).unwrap();
    std::fs::remove_dir_all(root).unwrap();
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn routed_production_lifecycle_capture_restore_and_reopen() {
    production_lifecycle_capture_restore_and_reopen(KeyMode::Customer, false).await;
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn routed_production_admission_selects_managed_and_customer_keys() {
    for mode in [KeyMode::Managed, KeyMode::Customer] {
        let (registry, member, records, root, _delivery) = routed_fixture(mode);
        let keys = crate::test_keys::key_delivery();
        let (admission, journal, remote) =
            production_owner(registry.clone(), member.clone(), &records, &root, &keys, mode, false)
                .await;
        admission.lifecycle().drain().await.unwrap();
        drop((admission, journal, remote, registry));
        member.close(std::time::Duration::from_secs(2)).unwrap();
        std::fs::remove_dir_all(root).unwrap();
    }
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn routed_production_admission_denies_provider_declaration_mismatch() {
    for mode in [KeyMode::Managed, KeyMode::Customer] {
        let (registry, member, records, root, _delivery) = routed_fixture(mode);
        let keys = crate::test_keys::key_delivery();
        let (admission, journal, remote) =
            production_owner(registry.clone(), member.clone(), &records, &root, &keys, mode, true)
                .await;
        drop((admission, journal, remote, registry));
        member.close(std::time::Duration::from_secs(2)).unwrap();
        std::fs::remove_dir_all(root).unwrap();
    }
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn routed_managed_lifecycle_capture_restore_and_reopen() {
    production_lifecycle_capture_restore_and_reopen(KeyMode::Managed, false).await;
}

#[tokio::test(flavor = "multi_thread", worker_threads = 2)]
async fn routed_paused_marker_reconciles_after_lost_result_and_restart() {
    production_lifecycle_capture_restore_and_reopen(KeyMode::Managed, true).await;
}

async fn production_lifecycle_capture_restore_and_reopen(mode: KeyMode, lose_result: bool) {
    use crate::native_recovery::{
        NativeRecoveryLimits, NativeRecoveryPhase, NativeRecoveryRuntime, NativeRestorePhase,
        NativeRestoreRequest, NativeRestoreSelection,
    };
    use terrabase_tbep::lifecycle::{LifecycleGrantAction, LifecycleOperationGrant};
    use terrabase_tbep::proto::terrabase::v1::{RestoreProvider, RestoreToTargetRequest};
    let (registry, member, records, root, _delivery) = routed_fixture(mode);
    let keys = crate::test_keys::key_delivery();
    let (admission, journal, remote) =
        production_owner(registry.clone(), member.clone(), &records, &root, &keys, mode, false)
            .await;
    // This must cross the real production input admission, including its
    // routed-support guard. Only the external object transport is controlled.
    let mut input = admission.resolve_native_backup_input(&records[0].database_id).unwrap();
    input.destinations = vec![(
        Provider::Oci,
        Arc::new(ObjectDestination::for_test(
            Provider::Oci,
            Arc::new(object_store::memory::InMemory::new()),
            4 * 1024 * 1024,
            16 * 1024 * 1024,
        )),
    )];
    input.historical_destinations.clear();
    let backups = Arc::new(
        NativeBackupRuntime::new(
            FleetLimits {
                streams: 2,
                staging_bytes_per_stream: 32 * 1024 * 1024,
                inventory_bytes_per_stream: 32 * 1024 * 1024,
                points_per_stream: 8,
            },
            [input],
        )
        .unwrap(),
    );
    let errors = Arc::new(std::sync::Mutex::new(Vec::new()));
    let executor = Arc::new(ObservedExecutor {
        inner: crate::production_data_runtime::ProductionRestoreExecutor::new(
            admission.clone(),
            backups.clone(),
        ),
        errors: errors.clone(),
        lose_result: lose_result.then(|| registry.clone()),
        executions: std::sync::atomic::AtomicUsize::new(0),
    });
    let (recovery, worker) = NativeRecoveryRuntime::new_with_fleet_gate(
        &root.join("recovery"),
        backups.fleet(),
        registry.clone(),
        NativeRecoveryLimits { pending_operations: 4, retained_operations: 8 },
        executor.clone(),
        Some(backups.fleet_gate()),
    )
    .unwrap();
    let operation =
        recovery.capture(records[0].database_id.clone(), "lifecycle-capture".into()).await.unwrap();
    let facts = tokio::time::timeout(std::time::Duration::from_secs(20), async {
        loop {
            let current =
                recovery.status(&records[0].database_id, &operation.operation_id).unwrap().unwrap();
            if current.phase == NativeRecoveryPhase::Complete {
                break current.facts.unwrap();
            }
            assert_ne!(current.phase, NativeRecoveryPhase::Failed, "{current:?}");
            tokio::time::sleep(std::time::Duration::from_millis(10)).await;
        }
    })
    .await
    .unwrap();
    let (envelope, provider) = recovery
        .resolve_recovery_envelope(
            &records[0].database_id,
            "point_id",
            &facts.recovery_point_id,
            &records[0].regions,
        )
        .await
        .unwrap()
        .unwrap();
    assert_eq!(provider, Provider::Oci);
    assert_eq!(envelope.logical_documents(), 2);
    let restore = identity(&envelope, &records[1], "production-routed-restore");
    let wire = RestoreToTargetRequest {
        operation_id: restore.operation_id.clone(),
        source_owner: format!("{}/{}", records[0].tenant_id, records[0].project_id),
        source_database: records[0].database_id.clone(),
        recovery_point_id: envelope.digest().into(),
        recovery_point_digest: envelope.digest().into(),
        target_owner: format!("{}/{}", records[1].tenant_id, records[1].project_id),
        target_database: records[1].database_id.clone(),
        journal_cut: restore.expected_journal_boundary.to_string(),
        restore_epoch: restore.restore_epoch,
        provider: RestoreProvider::Oci as i32,
        ..Default::default()
    };
    let proof = journal.accepted().unwrap();
    let now: i64 = std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .unwrap()
        .as_millis()
        .try_into()
        .unwrap();
    let grant = LifecycleOperationGrant {
        version: 1,
        domain: "terrascale-lifecycle-operation-v1".into(),
        environment: "production".into(),
        tenant: records[1].tenant_id.clone(),
        project: records[1].project_id.clone(),
        database: records[1].database_id.clone(),
        region: "GRU".into(),
        member: records[1].member_id.clone(),
        action: LifecycleGrantAction::RestoreToTarget,
        operation_id: restore.operation_id.clone(),
        accepted_proof_digest: proof.proof_digest.clone(),
        accepted_placement_revision: proof.proposal_revision,
        authority_epoch: 7,
        authority_lease: "local-test-lease".into(),
        accepted_restore_request_digest:
            crate::production_lifecycle_control::restore_request_digest(&wire).unwrap(),
        issued_at_unix_millis: now,
        expires_at_unix_millis: now + 60_000,
    };
    let encoded = signed_control(&grant, "authority", 1);
    let verified = journal.verify_lifecycle_grant(&encoded).unwrap();
    journal.admit_lifecycle_operation(&verified).unwrap();
    registry.pause(&records[1].database_id, "production-restore-pause").unwrap();
    let request = NativeRestoreRequest {
        operation_id: wire.operation_id,
        source_owner: wire.source_owner,
        source_database_id: wire.source_database,
        recovery_point_id: wire.recovery_point_id.clone(),
        recovery_point_digest: wire.recovery_point_digest,
        target_owner: wire.target_owner,
        target_database_id: wire.target_database,
        journal_cut: wire.journal_cut,
        restore_epoch: wire.restore_epoch,
        authority_epoch: 7,
        authority_lease: "local-test-lease".into(),
        accepted_proof_digest: proof.proof_digest,
        operation_grant: encoded,
        provider: "oci".into(),
        selection: NativeRestoreSelection::PointId(wire.recovery_point_id),
        residency: "GRU".into(),
        placement: vec!["GRU".into()],
    };
    recovery.admit_restore(request.clone()).unwrap();
    let (recovery, worker) = if lose_result {
        tokio::time::timeout(std::time::Duration::from_secs(20), async {
            loop {
                let operation = recovery.restore_status(&request.operation_id).unwrap().unwrap();
                if operation.phase == NativeRestorePhase::Uncertain {
                    break;
                }
                assert_ne!(operation.phase, NativeRestorePhase::Failed, "{operation:?}");
                tokio::time::sleep(std::time::Duration::from_millis(10)).await;
            }
        })
        .await
        .unwrap();
        worker.shutdown().await.unwrap();
        let target = registry
            .retained_snapshot()
            .unwrap()
            .into_iter()
            .find(|entry| entry.database.record.database_id == request.target_database_id)
            .unwrap();
        let host = target.database.host.engine_host();
        assert!(host.admitted_shard_read_cut().is_err(), "paused public cut must stay denied");
        assert!(host.verify_published_restore(&restore).unwrap().is_some());
        let mut foreign_identity = restore.clone();
        foreign_identity.expected_manifest_digest = "b".repeat(64);
        assert!(host.verify_published_restore(&foreign_identity).is_err());
        foreign_identity = restore.clone();
        foreign_identity.operation_id.push_str("-foreign");
        assert!(host.verify_published_restore(&foreign_identity).unwrap().is_none());
        assert!(host.admitted_shard_read_cut().is_err(), "verification must not resume");
        drop(target);
        drop(recovery);
        let (reopened, restarted_worker) = NativeRecoveryRuntime::new_with_fleet_gate(
            &root.join("recovery"),
            backups.fleet(),
            registry.clone(),
            NativeRecoveryLimits { pending_operations: 4, retained_operations: 8 },
            executor.clone(),
            Some(backups.fleet_gate()),
        )
        .unwrap();
        assert_eq!(
            reopened.retry_restore_readback(&request.operation_id).unwrap().phase,
            NativeRestorePhase::Verifying,
        );
        (reopened, restarted_worker)
    } else {
        (recovery, worker)
    };
    let completed = tokio::time::timeout(std::time::Duration::from_secs(20), async {
        loop {
            let current = recovery.restore_status(&request.operation_id).unwrap().unwrap();
            if current.phase == NativeRestorePhase::Complete {
                break current;
            }
            assert!(
                !matches!(
                    current.phase,
                    NativeRestorePhase::Failed | NativeRestorePhase::Uncertain
                ),
                "{current:?}; executor failures: {:?}",
                errors.lock().unwrap()
            );
            tokio::time::sleep(std::time::Duration::from_millis(10)).await;
        }
    })
    .await
    .unwrap();
    assert_eq!(completed.receipt.unwrap().restored_documents, 2);
    assert_eq!(
        executor.executions.load(std::sync::atomic::Ordering::SeqCst),
        1,
        "published marker reconciliation must not repeat provider execution"
    );
    let verified_facts = recovery.verified_restore_source_facts(&request).await.unwrap();
    assert_eq!(verified_facts.row_count, 2);
    assert_eq!(verified_facts.checksum, envelope.logical_checksum());
    assert_eq!(
        recovery.admit_restore(request.clone()).unwrap().phase,
        NativeRestorePhase::Complete
    );
    let mut foreign = request.clone();
    foreign.recovery_point_digest = "a".repeat(64);
    assert!(
        recovery.admit_restore(foreign).is_err(),
        "same operation cannot select a foreign envelope"
    );
    worker.shutdown().await.unwrap();
    drop(recovery);
    let (reopened, worker) = NativeRecoveryRuntime::new_with_fleet_gate(
        &root.join("recovery"),
        backups.fleet(),
        registry.clone(),
        NativeRecoveryLimits { pending_operations: 4, retained_operations: 8 },
        executor.clone(),
        Some(backups.fleet_gate()),
    )
    .unwrap();
    assert_eq!(
        reopened.restore_status(&request.operation_id).unwrap().unwrap().phase,
        NativeRestorePhase::Complete
    );
    let target = registry.resolve(&records[1].database_id).unwrap().unwrap();
    let cut = target.host.engine_host().admitted_shard_read_cut().unwrap().unwrap();
    assert_eq!(cut.participants.len(), 2);
    let queries = target.host.engine().queries();
    let plan = queries
        .compile(terrabase::QueryInput::sql(
            terrabase::SqlQueryText::try_new("SELECT _id FROM items ORDER BY _id").unwrap(),
        ))
        .unwrap();
    let result = queries.execute_admitted(&plan, &cut).unwrap();
    assert_eq!(
        result.rows().iter().map(|row| row.document().id().as_str()).collect::<Vec<_>>(),
        ["a", "z"]
    );
    drop((result, cut, target));
    if lose_result {
        registry.pause(&request.target_database_id, "revoke-after-verification").unwrap();
        let target = registry
            .retained_snapshot()
            .unwrap()
            .into_iter()
            .find(|entry| entry.database.record.database_id == request.target_database_id)
            .unwrap();
        let engine = target.database.host.engine();
        let tenant = TenantId::try_new(&records[1].tenant_id).unwrap();
        engine
            .lifecycle_prepare_deletion(&engine.scope_for(&tenant), "revoked-restore-check")
            .unwrap();
        assert!(target.database.host.engine_host().verify_published_restore(&restore).is_err());
        assert!(target.database.host.engine_host().admitted_shard_read_cut().is_err());
        assert!(registry.resolve(&request.target_database_id).unwrap().is_none());
    }
    worker.shutdown().await.unwrap();
    drop(reopened);
    backups.drain().unwrap();
    admission.lifecycle().drain().await.unwrap();
    drop((admission, backups, journal, remote, registry));
    member.close(std::time::Duration::from_secs(2)).unwrap();
    std::fs::remove_dir_all(root).unwrap();
}
