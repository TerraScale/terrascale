//! Durable participant acceptance cases against the real SQLite adapter.
use super::super::{context_frontier, cross_file};
use super::*;
use crate::storage::{CommitRejection, ParticipantIdentity, PrivateMetadataWrite};

fn identity(intent: &CommitIntent) -> ParticipantIdentity {
    ParticipantIdentity {
        tenant: intent.tenant.clone(),
        database_incarnation: 17,
        operation: intent.operation.clone(),
        participant: "shard-1".into(),
        owner: "database-1".into(),
        digest: [42; 32],
        epoch: 9,
        snapshot: intent.snapshot_sequence,
    }
}

fn coordinator_record(id: &ParticipantIdentity, origin: &str) -> DocumentValue {
    use std::collections::BTreeMap;
    DocumentValue::Object(BTreeMap::from([
        ("tenant".into(), DocumentValue::String(id.tenant.to_string())),
        ("coordinator_origin".into(), DocumentValue::String(origin.into())),
        ("incarnation".into(), DocumentValue::String(format!("{:032x}", id.database_incarnation))),
        ("authority_epoch".into(), DocumentValue::Integer(id.epoch as i64)),
        ("route_generation".into(), DocumentValue::Integer(1)),
        ("read_frontier".into(), DocumentValue::Integer(id.snapshot.get() as i64)),
        ("write_digest".into(), DocumentValue::String(hex::encode(id.digest))),
        (
            "participants".into(),
            DocumentValue::Array(vec![DocumentValue::Object(BTreeMap::from([
                ("name".into(), DocumentValue::String(id.participant.clone())),
                ("sequence".into(), DocumentValue::Integer(id.snapshot.get() as i64)),
            ]))]),
        ),
        ("operation".into(), DocumentValue::String(id.operation.to_string())),
        ("decision_epoch".into(), DocumentValue::Integer(1)),
        ("phase".into(), DocumentValue::String("prepare".into())),
    ]))
}

#[test]
fn prepared_cross_file_dependencies_are_retained_without_fabricating_applied_frontier() {
    let dir = location("cross-file-dependencies");
    let mut storage = SqliteStorage::open(&dir).unwrap();
    let dependencies =
        crate::engine::merge::DocumentContext::from_frontier([("remote-root".into(), 7)])
            .to_bytes();
    let mut request = intent(
        "dependent-child",
        vec![put(document("a", "items", "value"), ExpectedVersion::New)],
        LocalDurability::Synced,
    );
    request.cross_file_dependencies = Some(dependencies.clone());
    assert!(!matches!(storage.commit(request.clone()), CommitResult::Committed(_)));
    let id = identity(&request);
    storage.prepare_cross_file_intent(&id, Some(&request)).unwrap();
    drop(storage);
    let mut storage = SqliteStorage::open(&dir).unwrap();
    let prepared = storage.resolve_cross_file_intent(&id).unwrap().unwrap();
    assert_eq!(prepared.intent.as_ref().unwrap().cross_file_dependencies, Some(dependencies));
    let CommitResult::Committed(receipt) = storage.install_cross_file_intent(&prepared) else {
        panic!("retained dependent intent must install")
    };
    let c = storage.raw_writer_connection();
    let context: Vec<u8> = c
        .query_row(
            "SELECT context FROM replication_publications WHERE local_seq=?1",
            [receipt.sequence.get() as i64],
            |r| r.get(0),
        )
        .unwrap();
    assert_eq!(
        crate::engine::merge::decode_context(&context).unwrap().frontier().get("remote-root"),
        Some(&7)
    );
    let tx = c.unchecked_transaction().unwrap();
    let applied = context_frontier(&tx, &tenant()).unwrap();
    assert!(
        !crate::engine::merge::decode_context(&applied)
            .unwrap()
            .frontier()
            .contains_key("remote-root")
    );
}

#[test]
fn prepared_cross_file_dependency_encoding_rejects_noncanonical_origins_and_empty_context() {
    assert!(cross_file::decode_dependencies(&[]).is_err());
    assert!(cross_file::decode_dependencies(&[1, 0, 0, 0, 0]).is_err());
    assert!(
        cross_file::decode_dependencies(
            &crate::engine::merge::DocumentContext::from_frontier([("bad\norigin".into(), 1)])
                .to_bytes()
        )
        .is_err()
    );
    assert!(
        cross_file::decode_dependencies(
            &crate::engine::merge::DocumentContext::from_frontier([("origin".into(), u64::MAX)])
                .to_bytes()
        )
        .is_err()
    );
}

#[test]
fn prepared_cross_file_reopens_installs_once_and_retains_exact_receipt() {
    let dir = location("cross-file-reopen");
    let mut storage = SqliteStorage::open(&dir).unwrap();
    let mut value = intent(
        "logical-op",
        vec![put(document("a", "items", "value"), ExpectedVersion::New)],
        LocalDurability::Synced,
    );
    value.metadata.push(PrivateMetadataWrite {
        key: "application:key".into(),
        value: Some(DocumentValue::String("metadata".into())),
    });
    let id = identity(&value);
    let original = storage.published_state();
    storage.prepare_cross_file_intent(&id, Some(&value)).unwrap();
    assert_eq!(storage.published_state(), original, "prepare must publish no staged effect");
    assert!(storage.finish_cross_file_intent(&id).is_err());
    drop(storage);
    let mut storage = SqliteStorage::open(&dir).unwrap();
    let prepared = storage.resolve_cross_file_intent(&id).unwrap().unwrap();
    assert_eq!(
        crate::storage::prepared_codec::encode(prepared.intent.as_ref().unwrap()).unwrap(),
        crate::storage::prepared_codec::encode(&value).unwrap()
    );
    let result = storage.install_cross_file_intent(&prepared);
    assert!(matches!(result, CommitResult::Committed(_)), "{result:?}");
    assert!(storage.abort_cross_file_intent(&id).is_err());
    drop(storage);
    let mut storage = SqliteStorage::open(&dir).unwrap();
    let prepared = storage.resolve_cross_file_intent(&id).unwrap().unwrap();
    assert_eq!(storage.install_cross_file_intent(&prepared), result);
    storage.finish_cross_file_intent(&id).unwrap();
    drop(storage);
    let mut storage = SqliteStorage::open(&dir).unwrap();
    let prepared = storage.resolve_cross_file_intent(&id).unwrap().unwrap();
    assert_eq!(storage.install_cross_file_intent(&prepared), result);
    let c = storage.raw_writer_connection();
    assert_eq!(
        c.query_row(
            "SELECT COUNT(*) FROM operation_facts WHERE client_request_ulid='logical-op'",
            [],
            |r| r.get::<_, i64>(0)
        )
        .unwrap(),
        1
    );
    assert_eq!(
        c.query_row("SELECT COUNT(*) FROM cross_file_authorization", [], |r| r.get::<_, i64>(0))
            .unwrap(),
        0
    );
}

#[test]
fn prepared_cross_file_reservation_blocks_all_mutation_classes_and_wrong_controls() {
    let dir = location("cross-file-reserve");
    let mut storage = SqliteStorage::open(&dir).unwrap();
    let value = intent(
        "held",
        vec![put(document("a", "items", "value"), ExpectedVersion::New)],
        LocalDurability::Synced,
    );
    let id = identity(&value);
    let mut invalid_control = value.clone();
    invalid_control.cross_file_control = Some(value.operation.clone());
    invalid_control.internal_maintenance = true;
    assert!(
        !matches!(storage.commit(invalid_control), CommitResult::Committed(_)),
        "control authority cannot carry documents even before reservation"
    );
    storage.prepare_cross_file_intent(&id, Some(&value)).unwrap();
    for (label, maintenance, replicated) in
        [("ordinary", false, false), ("maintenance", true, false), ("replication", false, true)]
    {
        let mut candidate = intent(
            label,
            vec![put(document("b", "items", "blocked"), ExpectedVersion::New)],
            LocalDurability::Synced,
        );
        candidate.internal_maintenance = maintenance;
        if replicated {
            candidate.writes = vec![StagedWrite::ReplicatedPut {
                document: document("b", "items", "blocked"),
                commit_ulid: "source".into(),
                hlc: 1,
                origin: "peer".into(),
                origin_sequence: 1,
                context: vec![],
            }];
        }
        assert!(!matches!(storage.commit(candidate), CommitResult::Committed(_)), "{label}");
    }
    assert!(storage.set_origin_epoch(1, false).is_err());
    assert!(
        storage
            .lifecycle_update(
                &tenant(),
                crate::engine::lifecycle_state::LifecycleCommand::Pause("pause".into())
            )
            .is_err()
    );
    assert!(
        storage
            .declare_merge_schema(
                &CollectionName::try_new("items").unwrap(),
                &crate::storage::MergeSchema::parse_canonical("value=pn-counter").unwrap()
            )
            .is_err()
    );
    assert!(
        storage
            .register_index_descriptor(&CollectionName::try_new("items").unwrap(), "value")
            .is_err()
    );
    let mut control = intent("control", vec![], LocalDurability::Synced);
    control.internal_maintenance = true;
    control.cross_file_control = Some(value.operation.clone());
    control.metadata =
        vec![PrivateMetadataWrite { key: "application:unrelated".into(), value: None }];
    assert!(!matches!(storage.commit(control.clone()), CommitResult::Committed(_)));
    control.operation = OperationId::try_new("allowed-control").unwrap();
    let record = coordinator_record(&id, &storage.local_origin);
    control.metadata = vec![
        PrivateMetadataWrite {
            key: format!("control:cross-file/v1/{}", id.operation),
            value: Some(record.clone()),
        },
        PrivateMetadataWrite { key: "control:cross-file-active/v1".into(), value: Some(record) },
    ];
    let mut foreign = control.clone();
    foreign.operation = OperationId::try_new("foreign-control").unwrap();
    if let Some(DocumentValue::Object(fields)) = &mut foreign.metadata[0].value {
        fields.insert("tenant".into(), DocumentValue::String("another-tenant".into()));
    }
    assert!(!matches!(storage.commit(foreign), CommitResult::Committed(_)));
    assert!(matches!(storage.commit(control), CommitResult::Committed(_)));
    let mut altered = storage.resolve_cross_file_intent(&id).unwrap().unwrap();
    altered.intent.as_mut().unwrap().hlc += 1;
    assert_eq!(
        storage.install_cross_file_intent(&altered),
        CommitResult::Rejected(CommitRejection::OperationIdReuse)
    );
    storage.abort_cross_file_intent(&id).unwrap();
    assert!(storage.resolve_cross_file_intent(&id).is_err());
    assert!(matches!(storage.commit(value), CommitResult::Committed(_)));
}

#[test]
fn prepared_cross_file_rejects_cas_before_reserving_and_none_never_mints_receipt() {
    let dir = location("cross-file-cas");
    let mut storage = SqliteStorage::open(&dir).unwrap();
    let seed = intent(
        "seed",
        vec![put(document("a", "items", "seed"), ExpectedVersion::New)],
        LocalDurability::Synced,
    );
    assert!(matches!(storage.commit(seed), CommitResult::Committed(_)));
    let mut stale = intent(
        "stale",
        vec![put(document("a", "items", "wrong"), ExpectedVersion::Exact(9))],
        LocalDurability::Synced,
    );
    stale.snapshot_sequence = storage.published_state().0;
    let id = identity(&stale);
    assert!(matches!(
        storage.prepare_cross_file_intent(&id, Some(&stale)),
        Err(CommitRejection::Conflict)
    ));
    assert!(storage.resolve_cross_file_intent(&id).unwrap().is_none());
    let before = storage.published_state();
    let none = storage.prepare_cross_file_intent(&id, None).unwrap();
    assert_eq!(
        storage.install_cross_file_intent(&none),
        CommitResult::Rejected(CommitRejection::Validation)
    );
    drop(storage);
    let mut storage = SqliteStorage::open(&dir).unwrap();
    storage.finish_cross_file_intent(&id).unwrap();
    assert_eq!(storage.published_state(), before);
    assert!(storage.resolve_cross_file_intent(&id).unwrap().unwrap().intent.is_none());
}

#[test]
fn prepared_cross_file_validates_unique_final_image_before_reservation() {
    let dir = location("cross-file-unique");
    let mut storage = SqliteStorage::open(&dir).unwrap();
    let collection = CollectionName::try_new("items").unwrap();
    assert!(
        storage
            .register_schema_index(&tenant(), &collection, "unique-email", &["email".into()], true)
            .unwrap()
    );
    let keyed = |id: &str| {
        Document::new(
            DocumentId::try_new(id).unwrap(),
            collection.clone(),
            DocumentValue::Object(std::collections::BTreeMap::from([(
                "email".into(),
                DocumentValue::String("same".into()),
            )])),
        )
    };
    let request = intent(
        "unique-conflict",
        vec![put(keyed("a"), ExpectedVersion::New), put(keyed("b"), ExpectedVersion::New)],
        LocalDurability::Synced,
    );
    assert!(matches!(
        storage.prepare_cross_file_intent(&identity(&request), Some(&request)),
        Err(CommitRejection::Conflict)
    ));
    assert!(storage.resolve_cross_file_intent(&identity(&request)).unwrap().is_none());
    assert_eq!(storage.published_state().0.get(), 0);
}

#[test]
fn prepared_cross_file_original_uniform_guard_accepts_only_own_contiguous_controls() {
    for interference in ["none", "foreign", "gap", "malformed"] {
        let dir = location("cross-file-uniform");
        let mut storage = SqliteStorage::open(&dir).unwrap();
        let mut request = intent(
            "uniform-op",
            vec![put(document("a", "items", "value"), ExpectedVersion::New)],
            LocalDurability::Synced,
        );
        request.expected_uniform_context = Some(vec![1, 0, 0, 0, 0]);
        let id = identity(&request);
        let mut control = intent("protocol-prepare", vec![], LocalDurability::Synced);
        control.internal_maintenance = true;
        control.cross_file_control = Some(id.operation.clone());
        let body = coordinator_record(&id, &storage.local_origin);
        control.metadata = vec![
            PrivateMetadataWrite {
                key: format!("control:cross-file/v1/{}", id.operation),
                value: Some(body.clone()),
            },
            PrivateMetadataWrite { key: "control:cross-file-active/v1".into(), value: Some(body) },
        ];
        assert!(matches!(storage.commit(control.clone()), CommitResult::Committed(_)));
        match interference {
            "foreign" => {
                let mut foreign = intent("foreign-publication", vec![], LocalDurability::Synced);
                foreign.snapshot_sequence = storage.published_state().0;
                foreign.metadata.push(PrivateMetadataWrite {
                    key: "application:key".into(),
                    value: Some(DocumentValue::Null),
                });
                assert!(matches!(storage.commit(foreign), CommitResult::Committed(_)));
            }
            "gap" => {
                storage
                    .raw_writer_connection()
                    .execute("DELETE FROM replication_publications", [])
                    .unwrap();
            }
            "malformed" => {
                storage
                    .raw_writer_connection()
                    .execute("UPDATE replication_publications SET private_payload=X'00'", [])
                    .unwrap();
            }
            _ => {}
        }
        let prepared = storage.prepare_cross_file_intent(&id, Some(&request));
        if interference != "none" {
            assert!(
                matches!(prepared, Err(CommitRejection::Conflict)),
                "{interference}: {prepared:?}"
            );
            continue;
        }
        let prepared = prepared.unwrap();
        assert_eq!(
            prepared.intent.as_ref().unwrap().expected_uniform_context,
            request.expected_uniform_context
        );
        control.operation = OperationId::try_new("protocol-decide").unwrap();
        control.snapshot_sequence = storage.published_state().0;
        for change in &mut control.metadata {
            if let Some(DocumentValue::Object(fields)) = &mut change.value {
                fields.insert("phase".into(), DocumentValue::String("commit".into()));
            }
        }
        assert!(matches!(storage.commit(control), CommitResult::Committed(_)));
        assert!(matches!(storage.install_cross_file_intent(&prepared), CommitResult::Committed(_)));
    }
}

fn allocation() -> crate::usage::VerifiedAllocation {
    use crate::usage::*;
    use aws_lc_rs::{
        digest::{SHA256, digest},
        rand::SystemRandom,
        signature::{ECDSA_P256_SHA256_ASN1_SIGNING, EcdsaKeyPair, KeyPair},
    };
    let rng = SystemRandom::new();
    let pkcs8 = EcdsaKeyPair::generate_pkcs8(&ECDSA_P256_SHA256_ASN1_SIGNING, &rng).unwrap();
    let key = EcdsaKeyPair::from_pkcs8(&ECDSA_P256_SHA256_ASN1_SIGNING, pkcs8.as_ref()).unwrap();
    let mut public = vec![
        0x30, 0x59, 0x30, 0x13, 0x06, 0x07, 0x2a, 0x86, 0x48, 0xce, 0x3d, 0x02, 0x01, 0x06, 0x08,
        0x2a, 0x86, 0x48, 0xce, 0x3d, 0x03, 0x01, 0x07, 0x03, 0x42, 0x00,
    ];
    public.extend_from_slice(key.public_key().as_ref());
    let key_id = hex::encode(digest(&SHA256, &public));
    let grant = AllocationGrant {
        schema_version: 2,
        kind: "regional_write_pool_refill".into(),
        refill_operation_id: "refill".into(),
        pool_id: "pool".into(),
        cycle_id: "cycle".into(),
        rate_nano_dollar: 1,
        organization_id: "tenant-a".into(),
        database_id: "db-1".into(),
        branch_id: "main".into(),
        holder_id: "holder".into(),
        region: "gru".into(),
        regions: vec!["gru".into()],
        placement: "placement".into(),
        local_units: 2,
        aggregate_units: 2,
        included_local_units: 0,
        paid_local_units: 2,
        included_aggregate_units: 0,
        paid_aggregate_units: 2,
        pool_sequence: 1,
        policy_epoch: 1,
        consent_epoch: 1,
        writer_epoch: 1,
        issued_at: "2026-01-01T00:00:00Z".into(),
        expires_at: "2099-01-01T00:00:00Z".into(),
    };
    let payload = serde_json::to_vec(&AllocationEnvelope { schema_version: 2, grant }).unwrap();
    let mut message = ALLOCATION_SIGNING_DOMAIN.to_vec();
    message.extend_from_slice(&payload);
    let signature = key.sign(&rng, &message).unwrap();
    let mut wrapped = b"TSCS".to_vec();
    wrapped.extend_from_slice(&[1, 1]);
    wrapped.extend_from_slice(digest(&SHA256, &public).as_ref());
    wrapped.extend_from_slice(&(signature.as_ref().len() as u16).to_be_bytes());
    wrapped.extend_from_slice(signature.as_ref());
    let wire = serde_json::to_vec(&SignedAllocation {
        payload,
        signature: wrapped,
        key_id: key_id.clone(),
    })
    .unwrap();
    VerifiedAllocation::verify(
        &wire,
        &key_id,
        &public,
        "gru",
        "placement",
        "tenant-a",
        "db-1",
        "holder",
        1,
        1,
        1,
        super::super::unix_millis() as u64,
    )
    .unwrap()
}

fn admitted(operation: &str, units: u64) -> CommitIntent {
    let mut value = intent(
        operation,
        vec![put(document("a", "items", "value"), ExpectedVersion::New)],
        LocalDurability::Synced,
    );
    value.hlc = (super::super::unix_millis() as u64) << 16;
    value.usage_admission = Some(
        crate::usage::UsageAdmissionContext::try_new(
            "project",
            "db-1",
            "partition",
            crate::regions::RegionCode::from_text("GRU").unwrap(),
            crate::usage::UsageFactId::try_new(operation).unwrap(),
            super::super::unix_millis() as u64,
            1,
            i128::from(units),
        )
        .unwrap(),
    );
    value.budget_allocation = Some(allocation());
    value.cross_file_billing = Some(crate::storage::CrossFileBilling {
        operation: value.operation.clone(),
        metering_participant: "shard-1".into(),
        participant: "shard-1".into(),
        total_write_units: units,
    });
    value
}

#[test]
fn prepared_cross_file_budget_is_admitted_before_decision_and_charged_once_on_reopen() {
    let dir = location("cross-file-budget");
    let mut storage = SqliteStorage::open(&dir).unwrap();
    let placement = crate::usage::UsagePlacement::try_new(
        tenant(),
        "project",
        "db-1",
        "partition",
        crate::regions::RegionCode::from_text("GRU").unwrap(),
        vec![crate::regions::RegionCode::from_text("GRU").unwrap()],
        "placement",
    )
    .unwrap();
    storage.configure_usage_placement(&placement).unwrap();
    let exhausted = admitted("exhausted", 3);
    assert!(matches!(
        storage.prepare_cross_file_intent(&identity(&exhausted), Some(&exhausted)),
        Err(CommitRejection::Validation)
    ));
    let value = admitted("budget-op", 2);
    let id = identity(&value);
    // The ordinary path must not accept the private full-request charge override.
    assert!(!matches!(storage.commit(value.clone()), CommitResult::Committed(_)));
    storage.prepare_cross_file_intent(&id, Some(&value)).unwrap();
    {
        let c = storage.raw_writer_connection();
        for table in
            ["usage_facts", "usage_budget_operations", "usage_budget_allocations", "documents"]
        {
            assert_eq!(
                c.query_row(&format!("SELECT COUNT(*) FROM {table}"), [], |r| r.get::<_, i64>(0))
                    .unwrap(),
                0,
                "prepare leaked {table}"
            );
        }
        assert!(c.execute("INSERT INTO usage_budget_allocations(tenant,operation,database_id,branch_id,holder_id,cycle_id,policy_epoch,consent_epoch,writer_epoch,placement,payload_digest,units_total,units_remaining,expires_at,pool_id,pool_sequence) VALUES('tenant-a','other','db-1','main','holder','cycle',1,1,1,'placement',X'00',100,100,'2099','pool',2)", []).is_err());
    }
    drop(storage);
    let mut storage = SqliteStorage::open(&dir).unwrap();
    let prepared = storage.resolve_cross_file_intent(&id).unwrap().unwrap();
    assert_eq!(prepared.intent.as_ref().unwrap().budget_allocation, value.budget_allocation);
    let result = storage.install_cross_file_intent(&prepared);
    assert!(matches!(result, CommitResult::Committed(_)), "{result:?}");
    assert_eq!(storage.install_cross_file_intent(&prepared), result);
    let c = storage.raw_writer_connection();
    assert_eq!(
        c.query_row("SELECT units_remaining FROM usage_budget_allocations", [], |r| r
            .get::<_, i64>(0))
            .unwrap(),
        0
    );
    assert_eq!(
        c.query_row("SELECT units FROM usage_budget_operations", [], |r| r.get::<_, i64>(0))
            .unwrap(),
        2
    );
    assert_eq!(
        c.query_row("SELECT COUNT(*) FROM usage_facts", [], |r| r.get::<_, i64>(0)).unwrap(),
        1
    );
}

#[test]
fn prepared_cross_file_nonmetering_participant_keeps_origin_without_second_charge() {
    let dir = location("cross-file-nonmetering");
    let mut storage = SqliteStorage::open(&dir).unwrap();
    let region = crate::regions::RegionCode::from_text("GRU").unwrap();
    storage
        .configure_usage_placement(
            &crate::usage::UsagePlacement::try_new(
                tenant(),
                "project",
                "db-1",
                "partition",
                region,
                vec![region],
                "placement",
            )
            .unwrap(),
        )
        .unwrap();
    let mut value = admitted("budget-op", 2);
    value.usage_admission = None;
    value.budget_allocation = None;
    value.cross_file_billing.as_mut().unwrap().participant = "shard-2".into();
    let mut id = identity(&value);
    id.participant = "shard-2".into();
    let prepared = storage.prepare_cross_file_intent(&id, Some(&value)).unwrap();
    assert!(matches!(storage.install_cross_file_intent(&prepared), CommitResult::Committed(_)));
    let c = storage.raw_writer_connection();
    assert_eq!(
        c.query_row("SELECT kind FROM documents", [], |r| r.get::<_, String>(0)).unwrap(),
        "origin"
    );
    assert_eq!(
        c.query_row("SELECT COUNT(*) FROM usage_facts", [], |r| r.get::<_, i64>(0)).unwrap(),
        0
    );
    assert_eq!(
        c.query_row("SELECT COUNT(*) FROM usage_budget_operations", [], |r| r.get::<_, i64>(0))
            .unwrap(),
        0
    );
    assert!(
        c.query_row("SELECT bytes FROM operation_facts", [], |r| r.get::<_, i64>(0)).unwrap() > 0
    );
}

#[test]
fn prepared_cross_file_root_metering_conserves_one_refill_across_different_children() {
    let mut root = SqliteStorage::open(&location("cross-file-root-budget")).unwrap();
    let grant = allocation();
    root.install_usage_budget(&tenant(), &grant).unwrap();
    let region = crate::regions::RegionCode::from_text("GRU").unwrap();
    root.configure_usage_placement(
        &crate::usage::UsagePlacement::try_new(
            tenant(),
            "project",
            "db-1",
            "partition",
            region,
            vec![region],
            "placement",
        )
        .unwrap(),
    )
    .unwrap();
    for (index, child_name) in ["shard-2", "shard-3"].into_iter().enumerate() {
        let mut child = SqliteStorage::open(&location("cross-file-child-budget")).unwrap();
        let operation = format!("different-child-{index}");
        let mut root_intent = admitted(&operation, 1);
        root_intent.budget_allocation = Some(grant.clone());
        root_intent.writes.clear(); // a real metering publication, no fabricated document
        root_intent.snapshot_sequence = root.published_state().0;
        let root_id = identity(&root_intent);
        let mut child_intent = intent(
            &operation,
            vec![put(document("a", "items", "child"), ExpectedVersion::New)],
            LocalDurability::Synced,
        );
        child_intent.cross_file_billing = root_intent.cross_file_billing.clone();
        child_intent.cross_file_billing.as_mut().unwrap().participant = child_name.into();
        let mut child_id = identity(&child_intent);
        child_id.participant = child_name.into();
        let root_prepared = root.prepare_cross_file_intent(&root_id, Some(&root_intent)).unwrap();
        let child_prepared =
            child.prepare_cross_file_intent(&child_id, Some(&child_intent)).unwrap();
        let root_receipt = root.install_cross_file_intent(&root_prepared);
        assert!(matches!(root_receipt, CommitResult::Committed(_)), "{root_receipt:?}");
        assert!(matches!(
            child.install_cross_file_intent(&child_prepared),
            CommitResult::Committed(_)
        ));
        assert_eq!(root.install_cross_file_intent(&root_prepared), root_receipt);
        root.finish_cross_file_intent(&root_id).unwrap();
        child.finish_cross_file_intent(&child_id).unwrap();
        assert_eq!(child.usage_budget_remaining(&tenant(), "refill").unwrap(), None);
    }
    let mut exhausted = admitted("third-child-exhausted", 1);
    exhausted.budget_allocation = Some(grant);
    exhausted.writes.clear();
    exhausted.snapshot_sequence = root.published_state().0;
    assert!(matches!(
        root.prepare_cross_file_intent(&identity(&exhausted), Some(&exhausted)),
        Err(CommitRejection::Validation)
    ));
    assert_eq!(root.usage_budget_remaining(&tenant(), "refill").unwrap(), Some(0));
    let c = root.raw_writer_connection();
    assert_eq!(
        c.query_row("SELECT COUNT(*) FROM usage_budget_allocations", [], |r| r.get::<_, i64>(0))
            .unwrap(),
        1
    );
    assert_eq!(
        c.query_row("SELECT SUM(units) FROM usage_budget_operations", [], |r| r.get::<_, i64>(0))
            .unwrap(),
        2
    );
    assert_eq!(
        c.query_row("SELECT COUNT(*) FROM usage_facts", [], |r| r.get::<_, i64>(0)).unwrap(),
        2
    );
    assert_eq!(
        c.query_row("SELECT COUNT(*) FROM documents", [], |r| r.get::<_, i64>(0)).unwrap(),
        0
    );
}
