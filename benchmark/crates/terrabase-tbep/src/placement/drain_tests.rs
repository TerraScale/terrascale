use super::*;
use base64::Engine as _;
use ring::signature::Ed25519KeyPair;
use serde_json::{Value, json};
use terrabase_cluster::production::DurableMembershipSource as _;
use terrabase_cluster::wal_route::DurableWalAssignmentSource as _;

fn authority_key() -> Ed25519KeyPair {
    Ed25519KeyPair::from_seed_unchecked(&[1; 32]).expect("authority key")
}

fn envelope(value: &Value) -> Vec<u8> {
    let payload = serde_json::to_vec(value).expect("payload");
    let mut message = b"terrabase-placement-transition-v1\0".to_vec();
    message.extend_from_slice(&payload);
    let signed = json!({
        "key_id": "authority",
        "payload": base64::engine::general_purpose::STANDARD.encode(payload),
        "signature": base64::engine::general_purpose::STANDARD.encode(authority_key().sign(&message)),
    });
    serde_json::to_vec(&signed).expect("signed envelope")
}

fn successor_request(
    fixture: &tests::Fixture,
    journal: &PlacementJournal,
    operation: &str,
    prior: &Value,
) -> (crate::proto::terrabase::v1::PlacementProposal, Value) {
    let mut request = fixture.request.clone();
    request.expected_revision = 1;
    let proposal = request.proposal.as_mut().expect("proposal");
    proposal.epoch = 2;
    proposal.generation = 2;
    let proposal = journal
        .stage("authenticated-orchestrator", operation, request)
        .expect("successor stage")
        .proposal
        .expect("successor proposal");
    let accepted = super::transition_tests::proof(&proposal, operation, Some(prior), &["GRU"]);
    // The prepared intent occupies revision 2; the eventual accepted successor
    // must carry a strictly newer journal revision.
    let mut accepted = accepted;
    accepted["journal_revision"] = Value::from(3);
    (proposal, accepted)
}

fn prepared_for(proof: &Value, operation: &str, journal_revision: u64) -> Vec<u8> {
    let request = proof.get("request").expect("request").clone();
    let request_digest = proof["request_digest"].as_str().expect("request digest");
    let prior_generation = proof["prior_generation"].as_u64().expect("prior generation");
    let prior_proof = proof["previous_proof_digest"].as_str().expect("prior proof");
    let prepared = json!({
        "schema_version": 1,
        "kind": "prepared_epoch",
        "request": request,
        "request_digest": request_digest,
        "prior_generation": prior_generation,
        "previous_proof_digest": prior_proof,
        "authority_epoch": 1,
        "lease_id": format!("lease-{operation}"),
        "journal_revision": journal_revision,
    });
    envelope(&prepared)
}

fn admitted_fixture() -> (tests::Fixture, PlacementJournal, Value) {
    let fixture = tests::Fixture::new();
    let journal = fixture.open();
    let proposal = journal
        .stage("authenticated-orchestrator", "first", fixture.request.clone())
        .expect("initial stage")
        .proposal
        .expect("initial proposal");
    journal.configure_transition_trust(super::transition_tests::trust(&proposal)).expect("trust");
    let proof = super::transition_tests::proof(&proposal, "first", None, &[]);
    journal
        .accept("first", &super::transition_tests::acceptance(&proof, 1))
        .expect("initial acceptance");
    (fixture, journal, proof)
}

#[test]
fn valid_prepared_drain_fences_admission_and_survives_restart() {
    let (fixture, journal, first) = admitted_fixture();
    let scope = *fixture.boundary.databases.read().unwrap().keys().next().unwrap();
    let (_, successor) = successor_request(&fixture, &journal, "next", &first);
    let prepared = prepared_for(&successor, "next", 2);
    journal.begin_transition_drain("next", &prepared).expect("begin drain");
    assert!(journal.load_admitted_placement().is_err());
    assert!(journal.load_assignment(scope).is_err());
    assert_eq!(journal.accepted().expect("historical acceptance").epoch, 1);
    drop(journal);

    let reopened = fixture.open();
    reopened
        .configure_transition_trust(super::transition_tests::trust(
            &fixture.request.proposal.clone().expect("proposal"),
        ))
        .expect("trust after restart");
    reopened.accepted_proposal().expect("historical proposal");
    assert!(reopened.load_admitted_placement().is_err());
    assert!(reopened.load_assignment(scope).is_err());
    assert_eq!(reopened.accepted().expect("historical acceptance").epoch, 1);
}

#[test]
fn invalid_or_conflicting_prepared_request_does_not_fence() {
    let (fixture, journal, first) = admitted_fixture();
    let scope = *fixture.boundary.databases.read().unwrap().keys().next().unwrap();
    let (_, successor) = successor_request(&fixture, &journal, "next", &first);
    let prepared = prepared_for(&successor, "next", 2);
    assert!(journal.begin_transition_drain("wrong", &prepared).is_err());
    assert!(journal.begin_transition_drain("next", b"not-signed").is_err());
    let mut tampered: Value = serde_json::from_slice(&prepared).expect("signed envelope");
    tampered["signature"] = Value::String("A".repeat(88));
    assert!(
        journal.begin_transition_drain("next", &serde_json::to_vec(&tampered).unwrap()).is_err()
    );
    assert!(journal.load_admitted_placement().is_ok());
    assert!(journal.load_assignment(scope).is_ok());
    assert!(journal.begin_transition_drain("next", &prepared).is_ok());
    let mut conflicting = successor.clone();
    conflicting["request"]["generation"] = Value::from(3);
    conflicting["request_digest"] = super::transition::hash(
        &serde_json::to_vec(
            &serde_json::from_value::<super::transition::Request>(conflicting["request"].clone())
                .unwrap(),
        )
        .expect("request"),
    )
    .into();
    assert!(matches!(
        journal.begin_transition_drain("next", &prepared_for(&conflicting, "next", 2)),
        Err(PlacementError::RetryMismatch)
    ));
    assert!(journal.load_admitted_placement().is_err());
}

#[test]
fn matching_successor_re_admits_and_exact_prior_retry_is_idempotent() {
    let (fixture, journal, first) = admitted_fixture();
    let scope = *fixture.boundary.databases.read().unwrap().keys().next().unwrap();
    let (_, successor) = successor_request(&fixture, &journal, "next", &first);
    let prepared = prepared_for(&successor, "next", 2);
    journal.begin_transition_drain("next", &prepared).expect("begin drain");
    assert!(journal.load_assignment(scope).is_err());
    assert!(journal.begin_transition_drain("next", &prepared).is_ok());
    let mut stale_proof = successor.clone();
    stale_proof["journal_revision"] = Value::from(2);
    let stale = super::transition_tests::acceptance(&stale_proof, 2);
    assert!(journal.accept("next", &stale).is_err());
    journal
        .accept("next", &super::transition_tests::acceptance(&successor, 2))
        .expect("successor acceptance");
    assert!(journal.load_assignment(scope).is_ok());
    assert!(journal.begin_transition_drain("next", &prepared).is_ok());
    let mut different = successor;
    different["request"]["generation"] = Value::from(3);
    assert!(journal.begin_transition_drain("next", &prepared_for(&different, "next", 3)).is_err());
}

#[test]
fn orphaned_persisted_drain_intent_fails_closed() {
    let (fixture, journal, _) = admitted_fixture();
    drop(journal);
    let connection = rusqlite::Connection::open(fixture.root.join("proposals.sqlite")).expect("db");
    connection
        .execute(
            "INSERT INTO transition_drains(prior_digest,operation,prepared,prepared_digest) VALUES (?1,?2,?3,?4)",
            rusqlite::params!["orphan", "orphan", b"corrupt", "corrupt"],
        )
        .expect("orphan row");
    drop(connection);
    let reopened = fixture.open();
    let proposal = fixture.request.proposal.clone().expect("proposal");
    assert!(
        reopened.configure_transition_trust(super::transition_tests::trust(&proposal)).is_err()
    );
    assert!(reopened.load_admitted_placement().is_err());
}

#[test]
fn signed_other_successor_cannot_bypass_persisted_intent() {
    let (fixture, journal, first) = admitted_fixture();
    let (proposal, successor) = successor_request(&fixture, &journal, "next", &first);
    journal.begin_transition_drain("next", &prepared_for(&successor, "next", 2)).unwrap();
    let trust = super::transition_tests::trust(&proposal);
    let prior = trust.decode(&super::transition_tests::acceptance(&first, 1).proof).unwrap();
    let mut other = super::transition_tests::proof(&proposal, "other", Some(&first), &["GRU"]);
    other["journal_revision"] = json!(3);
    let verified = trust.decode(&super::transition_tests::acceptance(&other, 2).proof).unwrap();
    verified.follows(Some(&prior)).unwrap();
    assert!(matches!(
        super::drain::match_successor(
            &journal.connection.lock().unwrap(),
            &trust,
            Some(&prior),
            &verified
        ),
        Err(PlacementError::Conflict)
    ));
    assert!(journal.load_admitted_placement().is_err());
}

#[test]
fn corrupted_existing_intent_fails_closed_live_and_after_reopen() {
    let (fixture, journal, first) = admitted_fixture();
    let (proposal, successor) = successor_request(&fixture, &journal, "next", &first);
    journal.begin_transition_drain("next", &prepared_for(&successor, "next", 2)).unwrap();
    journal
        .connection
        .lock()
        .unwrap()
        .execute(
            "UPDATE transition_drains SET prepared_digest='corrupt' WHERE operation='next'",
            [],
        )
        .unwrap();
    assert!(journal.accepted().is_err());
    assert!(journal.load_admitted_placement().is_err());
    drop(journal);
    let reopened = fixture.open();
    assert!(
        reopened.configure_transition_trust(super::transition_tests::trust(&proposal)).is_err()
    );
    assert!(reopened.load_admitted_placement().is_err());
}

#[test]
fn drain_intent_process_exit_child() {
    let Some(path) = std::env::var_os("TERRABASE_DRAIN_INTENT_EXIT_PATH") else { return };
    let decode = |name| {
        base64::engine::general_purpose::STANDARD.decode(std::env::var(name).unwrap()).unwrap()
    };
    let request =
        pb::PlacementStageRequest::decode(decode("TERRABASE_DRAIN_INTENT_REQUEST").as_slice())
            .unwrap();
    let proposal = request.proposal.unwrap();
    let assignment = &proposal.assignments[0];
    let scope =
        DatabaseRouteScope::try_new(&assignment.tenant, &assignment.project, &assignment.database)
            .unwrap();
    let boundary = PlacementBoundary::new(
        &proposal.environment,
        &proposal.cluster,
        &assignment.writer_member,
        1,
        1,
        [(scope, Some("GRU".into()), BTreeSet::from(["GRU".into()]))],
    )
    .unwrap();
    let journal =
        PlacementJournal::open(&std::path::Path::new(&path).join("proposals.sqlite"), boundary)
            .unwrap();
    journal.configure_transition_trust(super::transition_tests::trust(&proposal)).unwrap();
    journal.begin_transition_drain("next", &decode("TERRABASE_DRAIN_INTENT_PREPARED")).unwrap();
    // Bypass Rust destructors and SQLite close after the committed intent.
    std::process::exit(73);
}

#[test]
fn committed_intent_survives_process_exit_without_sqlite_close() {
    let (fixture, journal, first) = admitted_fixture();
    let (_, successor) = successor_request(&fixture, &journal, "next", &first);
    let prepared = prepared_for(&successor, "next", 2);
    drop(journal);
    let status = std::process::Command::new(std::env::current_exe().unwrap())
        .args(["--exact", "placement::drain_tests::drain_intent_process_exit_child", "--nocapture"])
        .env("TERRABASE_DRAIN_INTENT_EXIT_PATH", &fixture.root)
        .env(
            "TERRABASE_DRAIN_INTENT_REQUEST",
            base64::engine::general_purpose::STANDARD.encode(fixture.request.encode_to_vec()),
        )
        .env(
            "TERRABASE_DRAIN_INTENT_PREPARED",
            base64::engine::general_purpose::STANDARD.encode(prepared),
        )
        .status()
        .unwrap();
    assert_eq!(status.code(), Some(73));
    let reopened = fixture.open();
    reopened
        .configure_transition_trust(super::transition_tests::trust(
            fixture.request.proposal.as_ref().unwrap(),
        ))
        .unwrap();
    assert_eq!(reopened.accepted().unwrap().epoch, 1);
    assert!(reopened.load_admitted_placement().is_err());
    let scope = *fixture.boundary.databases.read().unwrap().keys().next().unwrap();
    assert!(reopened.load_assignment(scope).is_err());
}
