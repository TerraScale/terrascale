use super::*;

fn now() -> i64 {
    i64::try_from(
        std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).unwrap().as_millis(),
    )
    .unwrap()
}

fn add_inventory(proof: &mut Value) {
    proof["member_receipt_keys"] = proof["request"]["members"]
        .as_array()
        .unwrap()
        .iter()
        .map(|member| {
            (
                member["identity"].as_str().unwrap().to_owned(),
                json!(
                    base64::engine::general_purpose::STANDARD.encode(key(3).public_key().as_ref())
                ),
            )
        })
        .collect::<serde_json::Map<_, _>>()
        .into();
}

struct JoinFixture {
    _origin: tests::Fixture,
    recipient: tests::Fixture,
    journal: PlacementJournal,
    pins: TransitionTrust,
    prior: Value,
    accepted: Value,
    checkpoint: Value,
    proposal: pb::PlacementProposal,
}

impl JoinFixture {
    fn new() -> Self {
        let origin = tests::Fixture::new();
        let original = origin.open();
        let prior_proposal = stage(&original, origin.request.clone(), "first");
        let pins = trust(&prior_proposal);
        let mut prior = proof(&prior_proposal, "first", None, &[]);
        add_inventory(&mut prior);
        let mut recipient = tests::Fixture::new();
        let member = recipient.boundary.member.clone();
        recipient.boundary = PlacementBoundary::new(
            &prior_proposal.environment,
            &prior_proposal.cluster,
            &member,
            2,
            2,
            origin
                .boundary
                .databases
                .read()
                .unwrap()
                .iter()
                .map(|(scope, (local, regions))| (*scope, local.clone(), regions.clone())),
        )
        .unwrap();
        let mut proposal = prior_proposal.clone();
        proposal.generation = 2;
        proposal.epoch = 2;
        let old_follower = proposal.assignments[0].follower_member.clone();
        proposal.assignments[0].follower_member = member.clone();
        for seat in &mut proposal.regions[0].members {
            if *seat == old_follower {
                *seat = member.clone();
            }
        }
        recipient.request = pb::PlacementStageRequest {
            expected_revision: 0,
            proposal: Some(proposal),
            declarations: Vec::new(),
        };
        let journal = recipient.open();
        journal.configure_transition_trust(pins.clone()).unwrap();
        let proposal = stage(&journal, recipient.request.clone(), "second");
        let mut accepted = proof(&proposal, "second", Some(&prior), &["GRU"]);
        add_inventory(&mut accepted);
        let nonce: String = journal
            .connection
            .lock()
            .unwrap()
            .query_row("SELECT nonce FROM placement_join_nonce", [], |row| row.get(0))
            .unwrap();
        let identity =
            MemberWorkloadIdentity::try_new(&proposal.environment, &proposal.cluster, &member)
                .unwrap()
                .as_str()
                .to_owned();
        let checkpoint = json!({
            "schema_version":1,"kind":"placement_join", "scope_id":accepted["request"]["scope_id"],
            "member_identity":identity,"member_region":"GRU","member_generation":2,
            "provider_resource_id":"ocid1.instance.test.join", "bootstrap_nonce":nonce,
            "prior_proof":sign("authority", &serde_json::to_vec(&prior).unwrap(), 1),
            "prior_proof_digest":hash(&serde_json::to_vec(&prior).unwrap()),
            "accepted_proof_digest":hash(&serde_json::to_vec(&accepted).unwrap()),
            "successor_request_digest":accepted["request_digest"],
            "stage_proposal_digest":hash(&proposal.encode_to_vec()),"proposal_revision":1,
            "authority_epoch":1,"lease_id":"lease-one","candidate_id":"gru-authority",
            "issued_at_unix_millis":now()-1000,"expires_at_unix_millis":now()+200000
        });
        Self { _origin: origin, recipient, journal, pins, prior, accepted, checkpoint, proposal }
    }

    fn request(&self, checkpoint: &Value) -> pb::PlacementAcceptRequest {
        let mut envelope: Value = serde_json::from_slice(&envelope(&self.accepted)).unwrap();
        envelope["join_checkpoint"] =
            serde_json::to_value(sign("authority", &serde_json::to_vec(checkpoint).unwrap(), 1))
                .unwrap();
        pb::PlacementAcceptRequest {
            proposal_revision: 1,
            proof: serde_json::to_vec(&envelope).unwrap(),
        }
    }
}

#[test]
fn fresh_join_commits_only_local_successor_and_replays_after_restart() {
    let fixture = JoinFixture::new();
    assert!(fixture.journal.accept("second", &acceptance(&fixture.accepted, 1)).is_err());
    let request = fixture.request(&fixture.checkpoint);
    let receipt = fixture.journal.accept("second", &request).unwrap();
    let retained = join::stored(&fixture.journal.connection.lock().unwrap()).unwrap().unwrap();
    let mut refreshed = fixture.checkpoint.clone();
    refreshed["issued_at_unix_millis"] = json!(now() - 5000);
    refreshed["expires_at_unix_millis"] = json!(now() - 1000);
    // A read-only exact epoch retry never installs a replacement checkpoint.
    assert_eq!(fixture.journal.accept("second", &fixture.request(&refreshed)).unwrap(), receipt);
    assert_eq!(
        join::stored(&fixture.journal.connection.lock().unwrap()).unwrap().unwrap(),
        retained
    );
    assert_eq!(
        fixture
            .journal
            .connection
            .lock()
            .unwrap()
            .query_row("SELECT count(*) FROM accepted", [], |row| row.get::<_, u64>(0))
            .unwrap(),
        1
    );
    let reopened = fixture.recipient.open();
    reopened.configure_transition_trust(fixture.pins.clone()).unwrap();
    assert_eq!(reopened.accepted().unwrap().epoch, 2);
    assert_eq!(reopened.accept("second", &request).unwrap(), receipt);
    let mut next = fixture.recipient.request.clone();
    next.expected_revision = 1;
    next.proposal.as_mut().unwrap().epoch = 3;
    next.proposal.as_mut().unwrap().generation = 3;
    let proposal = stage(&reopened, next, "third");
    let mut third = proof(&proposal, "third", Some(&fixture.accepted), &["GRU"]);
    add_inventory(&mut third);
    reopened.accept("third", &acceptance(&third, 2)).unwrap();
    assert_eq!(reopened.accepted().unwrap().epoch, 3);
    assert_ne!(fixture.prior["request"]["members"], fixture.accepted["request"]["members"]);
}

#[test]
fn join_rejects_wrong_scope_member_nonce_stage_freshness_and_floors_without_mutation() {
    let fixture = JoinFixture::new();
    for (field, value) in [
        ("scope_id", json!("other/cluster")),
        ("member_identity", json!("other-member")),
        ("member_region", json!("FRA")),
        ("bootstrap_nonce", json!("f".repeat(64))),
        ("stage_proposal_digest", json!("f".repeat(64))),
        ("proposal_revision", json!(2)),
        ("accepted_proof_digest", json!("f".repeat(64))),
        ("authority_epoch", json!(0)),
        ("expires_at_unix_millis", json!(now() - 1)),
    ] {
        let mut invalid = fixture.checkpoint.clone();
        invalid[field] = value;
        assert!(fixture.journal.accept("second", &fixture.request(&invalid)).is_err(), "{field}");
        assert!(join::stored(&fixture.journal.connection.lock().unwrap()).unwrap().is_none());
        assert_eq!(fixture.journal.accepted().unwrap_err(), PlacementError::NotFound);
    }
    fixture
        .journal
        .connection
        .lock()
        .unwrap()
        .execute("INSERT INTO lifecycle_authority VALUES(1,2)", [])
        .unwrap();
    assert!(fixture.journal.accept("second", &fixture.request(&fixture.checkpoint)).is_err());
    assert!(join::stored(&fixture.journal.connection.lock().unwrap()).unwrap().is_none());
}

#[test]
fn random_nonce_persists_but_fresh_storage_never_reuses_it() {
    let fixture = JoinFixture::new();
    let signer_der = Ed25519KeyPair::generate_pkcs8(&ring::rand::SystemRandom::new()).unwrap();
    let identity = fixture.checkpoint["member_identity"].as_str().unwrap();
    let signer =
        transition::TransitionReceiptSigner::from_pkcs8(identity.into(), signer_der.as_ref())
            .unwrap();
    let receipt = fixture.journal.latest().unwrap();
    let first = fixture.journal.sign_join_challenge(&receipt, &signer).unwrap();
    fixture.journal.accept("second", &fixture.request(&fixture.checkpoint)).unwrap();
    assert_eq!(fixture.journal.sign_join_challenge(&receipt, &signer).unwrap(), first);
    let reopened = fixture.recipient.open();
    assert_eq!(reopened.sign_join_challenge(&receipt, &signer).unwrap(), first);
    let mut replacement = tests::Fixture::new();
    replacement.boundary = fixture.recipient.boundary.clone();
    let fresh = replacement.open();
    fresh.configure_transition_trust(fixture.pins.clone()).unwrap();
    stage(&fresh, fixture.recipient.request.clone(), "second");
    assert_ne!(fresh.sign_join_challenge(&fresh.latest().unwrap(), &signer).unwrap(), first);
    assert!(fresh.accept("second", &fixture.request(&fixture.checkpoint)).is_err());
    assert_eq!(fixture.proposal.epoch, 2);
}

#[test]
fn checkpoint_and_acceptance_roll_back_together_on_storage_failure() {
    let fixture = JoinFixture::new();
    fixture.journal.connection.lock().unwrap().execute_batch("CREATE TRIGGER fail_accept BEFORE INSERT ON accepted BEGIN SELECT RAISE(ABORT,'injected'); END;").unwrap();
    assert!(fixture.journal.accept("second", &fixture.request(&fixture.checkpoint)).is_err());
    assert!(join::stored(&fixture.journal.connection.lock().unwrap()).unwrap().is_none());
    fixture.journal.connection.lock().unwrap().execute_batch("DROP TRIGGER fail_accept").unwrap();
    fixture.journal.accept("second", &fixture.request(&fixture.checkpoint)).unwrap();
}
