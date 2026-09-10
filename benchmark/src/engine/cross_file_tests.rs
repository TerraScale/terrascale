use super::cross_file::{CrossFileDecision, CrossFileIdentity, CrossFileRecord};
use super::{Engine, EngineConfig, EngineError};
use crate::types::{OperationId, TenantId};
use std::collections::BTreeMap;

fn open(tag: &str) -> (std::path::PathBuf, Engine) {
    let path =
        std::env::temp_dir().join(format!("terrabase-cross-file-{tag}-{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&path);
    (path.clone(), Engine::open(EngineConfig::durable(path).unwrap()).unwrap())
}

fn identity(engine: &Engine, operation: &str) -> CrossFileIdentity {
    CrossFileIdentity {
        tenant: "tenant".into(),
        coordinator_origin: engine.replication_origin().unwrap(),
        incarnation: u128::from_be_bytes(engine.database_id().as_bytes().to_owned()),
        authority_epoch: 1,
        route_generation: 0,
        read_frontier: 1,
        write_digest: [operation.as_bytes()[0]; 32],
        participants: BTreeMap::from([(String::from("source"), 1)]),
        operation: OperationId::try_new(operation).unwrap(),
        decision_epoch: 1,
    }
}

#[test]
fn decisions_are_independent_terminal_and_restart_safe() {
    let (path, engine) = open("terminal");
    let scope = engine.scope_for(&TenantId::try_new("tenant").unwrap());
    let first = identity(&engine, "logical-first");
    let second = identity(&engine, "logical-second");
    engine.prepare_cross_file(&scope, first.clone()).unwrap();
    assert_eq!(engine.prepare_cross_file(&scope, second.clone()), Err(EngineError::Conflict));
    assert_eq!(
        engine.decide_cross_file(&scope, &first, CrossFileDecision::Committed).unwrap().0.decision,
        Some(CrossFileDecision::Committed)
    );
    assert_eq!(engine.read_snapshot(&scope), Err(EngineError::Conflict));
    engine.advance_cross_file_visible(&scope, &first, first.participants.clone()).unwrap();
    engine.prepare_cross_file(&scope, second.clone()).unwrap();
    assert_eq!(
        engine.decide_cross_file(&scope, &second, CrossFileDecision::Aborted).unwrap().0.decision,
        Some(CrossFileDecision::Aborted)
    );
    assert!(engine.read_cross_file_active(&scope).unwrap().is_none());
    assert!(engine.read_snapshot(&scope).is_ok());
    assert_eq!(
        engine.decide_cross_file(&scope, &first, CrossFileDecision::Committed).unwrap().0.decision,
        Some(CrossFileDecision::Committed)
    );
    assert_eq!(
        engine.decide_cross_file(&scope, &first, CrossFileDecision::Aborted),
        Err(EngineError::Conflict)
    );
    assert_eq!(
        engine.decide_cross_file(&scope, &second, CrossFileDecision::Committed),
        Err(EngineError::Conflict)
    );
    engine.shutdown().unwrap();
    let engine = Engine::open(EngineConfig::durable(&path).unwrap()).unwrap();
    let scope = engine.scope_for(&TenantId::try_new("tenant").unwrap());
    assert_eq!(
        engine.read_cross_file_decision(&scope, &first.operation).unwrap().unwrap().decision,
        Some(CrossFileDecision::Committed)
    );
    assert_eq!(
        engine.read_cross_file_decision(&scope, &second.operation).unwrap().unwrap().decision,
        Some(CrossFileDecision::Aborted)
    );
    engine.shutdown().unwrap();
    std::fs::remove_dir_all(path).unwrap();
}

#[test]
fn invalid_identity_and_foreign_owner_are_rejected() {
    let (_path, engine) = open("invalid");
    let scope = engine.scope_for(&TenantId::try_new("tenant").unwrap());
    let mut foreign_origin = identity(&engine, "foreign-origin");
    foreign_origin.coordinator_origin = "another-root-origin".into();
    assert_eq!(engine.prepare_cross_file(&scope, foreign_origin), Err(EngineError::Conflict));
    let mut invalid = identity(&engine, "invalid");
    invalid.participants.insert(String::from("overflow"), u64::MAX);
    assert_eq!(
        engine.prepare_cross_file(&scope, invalid).map(|_| ()),
        Err(EngineError::InvalidDocument)
    );
    let mut foreign = identity(&engine, "foreign");
    foreign.incarnation = 99;
    assert_eq!(engine.prepare_cross_file(&scope, foreign), Err(EngineError::Conflict));
    for mutate in [
        |value: &mut CrossFileIdentity| value.authority_epoch = 1 << 63,
        |value: &mut CrossFileIdentity| value.route_generation = 1 << 63,
        |value: &mut CrossFileIdentity| value.read_frontier = 1 << 63,
        |value: &mut CrossFileIdentity| value.decision_epoch = 1 << 63,
    ] {
        let mut overflow = identity(&engine, "overflow");
        mutate(&mut overflow);
        assert_eq!(engine.prepare_cross_file(&scope, overflow), Err(EngineError::InvalidDocument));
    }
    engine.shutdown().unwrap();
}

#[test]
fn terminal_record_preserves_identity_and_decision() {
    let (_path, engine) = open("record");
    let value = CrossFileRecord {
        identity: identity(&engine, "record"),
        decision: Some(CrossFileDecision::Committed),
    };
    assert_eq!(value.identity.decision_epoch, 1);
    engine.shutdown().unwrap();
}

#[test]
fn active_fence_blocks_new_cuts_but_preserves_pinned_reads_and_later_writes() {
    use crate::types::{
        CollectionName, Document, DocumentId, DocumentValue, ExpectedVersion, LocalDurability,
    };
    let (_path, engine) = open("read-fence");
    let scope = engine.scope_for(&TenantId::try_new("tenant").unwrap());
    let collection = CollectionName::try_new("items").unwrap();
    let id = DocumentId::try_new("item").unwrap();
    let mut write = engine
        .begin(
            &scope,
            engine.read_snapshot(&scope).unwrap(),
            OperationId::try_new("before").unwrap(),
        )
        .unwrap();
    write
        .put(
            Document::new(id.clone(), collection.clone(), DocumentValue::Integer(1)),
            ExpectedVersion::Any,
        )
        .unwrap();
    assert!(matches!(write.commit(LocalDurability::Synced), super::CommitOutcome::Committed(_)));
    let pinned = engine
        .begin(
            &scope,
            engine.read_snapshot(&scope).unwrap(),
            OperationId::try_new("pinned").unwrap(),
        )
        .unwrap();
    let mut pending = engine
        .begin(
            &scope,
            engine.read_snapshot(&scope).unwrap(),
            OperationId::try_new("racing-write").unwrap(),
        )
        .unwrap();
    pending
        .put(
            Document::new(id.clone(), collection.clone(), DocumentValue::Integer(2)),
            ExpectedVersion::Any,
        )
        .unwrap();
    let identity = identity(&engine, "fenced-batch");
    engine.prepare_cross_file(&scope, identity.clone()).unwrap();
    assert_eq!(engine.read_snapshot(&scope), Err(EngineError::Conflict));
    assert_eq!(
        pinned.read_in_collection(&collection, &id).unwrap().unwrap().value(),
        &DocumentValue::Integer(1)
    );
    assert!(matches!(
        pending.commit(LocalDurability::Synced),
        super::CommitOutcome::Rejected(super::CommitError::Conflict)
    ));
    engine.decide_cross_file(&scope, &identity, CrossFileDecision::Committed).unwrap();
    assert_eq!(
        engine.read_cross_file_active(&scope).unwrap().unwrap().decision,
        Some(CrossFileDecision::Committed)
    );
    let installed = BTreeMap::from([(String::from("source"), 9)]);
    engine.advance_cross_file_visible(&scope, &identity, installed).unwrap();
    let mut later = engine
        .begin(
            &scope,
            engine.read_snapshot(&scope).unwrap(),
            OperationId::try_new("later").unwrap(),
        )
        .unwrap();
    later
        .put(
            Document::new(id.clone(), collection.clone(), DocumentValue::Integer(3)),
            ExpectedVersion::Any,
        )
        .unwrap();
    assert!(matches!(later.commit(LocalDurability::Synced), super::CommitOutcome::Committed(_)));
    let current = engine
        .begin(
            &scope,
            engine.read_snapshot(&scope).unwrap(),
            OperationId::try_new("current").unwrap(),
        )
        .unwrap();
    assert_eq!(
        current.read_in_collection(&collection, &id).unwrap().unwrap().value(),
        &DocumentValue::Integer(3)
    );
    assert_eq!(
        pinned.read_in_collection(&collection, &id).unwrap().unwrap().value(),
        &DocumentValue::Integer(1)
    );
}

#[test]
fn participant_prepare_resolves_exactly_across_engine_reopen() {
    use crate::storage::participant::{ParticipantIdentity, ParticipantPrepare, ParticipantWrite};
    use crate::types::{
        CollectionName, Document, DocumentId, DocumentValue, ExpectedVersion, StorageSequence,
    };
    let (path, engine) = open("participant");
    let tenant = TenantId::try_new("tenant").unwrap();
    let operation = OperationId::try_new("participant-op").unwrap();
    let write = ParticipantWrite::Put {
        document: Document::new(
            DocumentId::try_new("doc").unwrap(),
            CollectionName::try_new("items").unwrap(),
            DocumentValue::Object(BTreeMap::new()),
        ),
        expected: ExpectedVersion::Any,
    };
    let payload = crate::storage::participant::encode_prepare_payload(
        std::slice::from_ref(&write),
        &[],
        &[],
        None,
    )
    .unwrap();
    let identity = ParticipantIdentity {
        tenant: tenant.clone(),
        database_incarnation: u128::from_be_bytes(engine.database_id().as_bytes().to_owned()),
        operation,
        participant: "file-a".into(),
        owner: "owner".into(),
        digest: *blake3::hash(&payload).as_bytes(),
        epoch: 1,
        snapshot: StorageSequence::new(1),
    };
    let prepare = ParticipantPrepare {
        identity: identity.clone(),
        writes: vec![write],
        metadata: Vec::new(),
        schema_envelopes: Vec::new(),
        replication_publication: None,
    };
    engine.storage.lock().unwrap().port.prepare_participant(&prepare).unwrap();
    assert!(engine.storage.lock().unwrap().port.resolve_participant(&identity).unwrap().is_some());
    engine.shutdown().unwrap();
    let reopened = Engine::open(EngineConfig::durable(path).unwrap()).unwrap();
    assert!(
        reopened.storage.lock().unwrap().port.resolve_participant(&identity).unwrap().is_some()
    );
    let mut foreign = identity.clone();
    foreign.owner = "foreign".into();
    assert!(reopened.storage.lock().unwrap().port.resolve_participant(&foreign).is_err());
    reopened.shutdown().unwrap();
}
