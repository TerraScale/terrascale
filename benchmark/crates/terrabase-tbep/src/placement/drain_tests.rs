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
    let _ = first;
}

#[test]
fn invalid_or_conflicting_prepared_request_does_not_fence() {
    let (fixture, journal, first) = admitted_fixture();
    let scope = *fixture.boundary.databases.read().unwrap().keys().next().unwrap();
    let (_, successor) = successor_request(&fixture, &journal, "next", &first);
    let prepared = prepared_for(&successor, "next", 2);
    assert!(journal.begin_transition_drain("wrong", &prepared).is_err());
    assert!(journal.begin_transition_drain("next", b"not-signed").is_err());
    assert!(journal.load_admitted_placement().is_ok());
    assert!(journal.load_assignment(scope).is_ok());
    assert!(journal.begin_transition_drain("next", &prepared).is_ok());
    let mut conflicting = successor.clone();
    conflicting["request"]["generation"] = Value::from(3);
    conflicting["request_digest"] =
        super::transition::hash(&serde_json::to_vec(&conflicting["request"]).expect("request"))
            .into();
    assert!(
        journal.begin_transition_drain("next", &prepared_for(&conflicting, "next", 2)).is_err()
    );
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
    let _ = first;
}

#[test]
fn orphaned_persisted_drain_intent_fails_closed() {
    let (fixture, journal, first) = admitted_fixture();
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
    reopened.configure_transition_trust(super::transition_tests::trust(&proposal)).expect("trust");
    assert!(reopened.load_admitted_placement().is_err());
    let _ = first;
}
