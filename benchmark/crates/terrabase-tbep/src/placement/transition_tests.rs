use super::*;
use base64::Engine as _;
use ring::signature::{Ed25519KeyPair, KeyPair as _};
use serde_json::{Value, json};
use terrabase::identity::{DatabaseUlid, ProjectUlid, TenantUlid};
use terrabase_cluster::production::DurableMembershipSource as _;
use terrabase_cluster::wal_route::DurableWalAssignmentSource as _;
use transition::{Request, Signed, TransitionMember, TransitionTrust, hash};

fn key(seed: u8) -> Ed25519KeyPair {
    Ed25519KeyPair::from_seed_unchecked(&[seed; 32]).unwrap()
}

fn sign(id: &str, payload: &[u8], seed: u8) -> Signed {
    let mut message = b"terrabase-placement-transition-v1\0".to_vec();
    message.extend_from_slice(payload);
    Signed {
        key_id: id.into(),
        payload: base64::engine::general_purpose::STANDARD.encode(payload),
        signature: base64::engine::general_purpose::STANDARD.encode(key(seed).sign(&message)),
    }
}

fn envelope(value: &Value) -> Vec<u8> {
    serde_json::to_vec(&sign("authority", &serde_json::to_vec(value).unwrap(), 1)).unwrap()
}

fn trust(proposal: &pb::PlacementProposal) -> TransitionTrust {
    let members = proposal
        .regions
        .iter()
        .flat_map(|region| &region.members)
        .map(|member| {
            let identity =
                MemberWorkloadIdentity::try_new(&proposal.environment, &proposal.cluster, member)
                    .unwrap();
            (identity.as_str().to_owned(), key(3).public_key().as_ref().to_vec())
        })
        .collect();
    TransitionTrust::new(
        BTreeMap::from([("authority".into(), key(1).public_key().as_ref().to_vec())]),
        BTreeMap::from([("genesis".into(), key(2).public_key().as_ref().to_vec())]),
        members,
    )
    .unwrap()
}

fn proof(
    proposal: &pb::PlacementProposal,
    operation: &str,
    previous: Option<&Value>,
    fenced_regions: &[&str],
) -> Value {
    let digest = hash(&proposal.encode_to_vec());
    let mut members: Vec<_> = proposal
        .regions
        .iter()
        .flat_map(|region| {
            region.members.iter().map(|member| TransitionMember {
                identity: MemberWorkloadIdentity::try_new(
                    &proposal.environment,
                    &proposal.cluster,
                    member,
                )
                .unwrap()
                .as_str()
                .to_owned(),
                region: region.region.clone(),
                placement_digest: digest.clone(),
            })
        })
        .collect();
    members.sort_by(|left, right| left.identity.cmp(&right.identity));
    let prior_epoch = previous.map_or(0, |prior| prior["request"]["epoch"].as_u64().unwrap());
    let prior_generation =
        previous.map_or(0, |prior| prior["request"]["generation"].as_u64().unwrap());
    let request = Request {
        operation_id: operation.into(),
        scope_id: format!("{}/{}", proposal.environment, proposal.cluster),
        prior_epoch,
        epoch: proposal.epoch,
        generation: proposal.generation,
        members,
    };
    let request_digest = hash(&serde_json::to_vec(&request).unwrap());
    let mut receipts = Vec::new();
    let mut receipt_digests = Vec::new();
    if let Some(prior) = previous {
        for region in fenced_regions {
            let member = prior["request"]["members"]
                .as_array()
                .unwrap()
                .iter()
                .find(|member| member["region"] == *region)
                .unwrap();
            let identity = member["identity"].as_str().unwrap();
            let receipt = serde_json::to_vec(&json!({"kind":"old_epoch_drained_fenced", "member_identity":identity,"scope_id":request.scope_id,"prior_epoch":prior_epoch,"prior_generation":prior_generation,"request_digest":request_digest,"frontier_digest":hash(b"durable frontier")})).unwrap();
            receipt_digests.push(hash(&receipt));
            receipts.push(sign(identity, &receipt, 3));
        }
    }
    receipt_digests.sort();
    let grant = previous.is_none().then(|| sign("genesis", &serde_json::to_vec(&json!({"kind":"genesis_grant","grant_id":"grant-one","request_digest":request_digest})).unwrap(), 2));
    json!({"schema_version":1,"kind":"accepted_epoch","request":request,"request_digest":request_digest,"authority_epoch":1,"journal_revision":proposal.epoch,"genesis_grant_id":if previous.is_none() {"grant-one"} else {""},"receipt_digests":receipt_digests,"drain_receipts":receipts,"prior_generation":prior_generation,"previous_proof_digest":previous.map(|prior| hash(&serde_json::to_vec(prior).unwrap())).unwrap_or_default(),"lease_id":"lease-one","genesis_grant":grant})
}

fn stage(
    journal: &PlacementJournal,
    request: pb::PlacementStageRequest,
    operation: &str,
) -> pb::PlacementProposal {
    journal.stage("authenticated-orchestrator", operation, request).unwrap().proposal.unwrap()
}

fn acceptance(value: &Value, revision: u64) -> pb::PlacementAcceptRequest {
    pb::PlacementAcceptRequest { proposal_revision: revision, proof: envelope(value) }
}

#[test]
fn pending_is_not_membership_and_acceptance_survives_restart_without_control_service() {
    let fixture = tests::Fixture::new();
    let scope = *fixture.boundary.databases.read().unwrap().keys().next().unwrap();
    let journal = fixture.open();
    let proposal = stage(&journal, fixture.request.clone(), "first");
    journal.configure_transition_trust(trust(&proposal)).unwrap();
    assert!(journal.load_admitted_placement().is_err());
    assert!(journal.load_assignment(scope).is_err());
    let first = proof(&proposal, "first", None, &[]);
    let request = acceptance(&first, 1);
    let accepted = journal.accept("first", &request).unwrap();
    assert_eq!(journal.accept("first", &request).unwrap(), accepted);
    assert!(journal.load_admitted_placement().is_ok());
    assert_eq!(journal.load_assignment(scope).unwrap().epoch, 1);
    drop(journal);
    let reopened = fixture.open();
    assert!(reopened.load_admitted_placement().is_err());
    reopened.configure_transition_trust(trust(&proposal)).unwrap();
    assert_eq!(reopened.accepted().unwrap(), accepted);
    assert_eq!(reopened.accept("first", &request).unwrap(), accepted);
    let mut pending = fixture.request.clone();
    pending.expected_revision = 1;
    pending.proposal.as_mut().unwrap().epoch = 2;
    pending.proposal.as_mut().unwrap().generation = 2;
    stage(&reopened, pending, "pending-successor");
    assert_eq!(reopened.accepted().unwrap().epoch, 1);
    assert_eq!(reopened.load_assignment(scope).unwrap().epoch, 1);
    assert!(reopened.load_admitted_placement().is_ok());
}

#[test]
fn authenticated_wrong_tuple_digest_genesis_and_signer_never_admit() {
    let fixture = tests::Fixture::new();
    let journal = fixture.open();
    let proposal = stage(&journal, fixture.request.clone(), "first");
    journal.configure_transition_trust(trust(&proposal)).unwrap();
    let valid = proof(&proposal, "first", None, &[]);
    for variant in 0..5 {
        let mut changed = valid.clone();
        match variant {
            0 => changed["genesis_grant_id"] = json!("wrong-grant"),
            1 => changed["request"]["epoch"] = json!(2),
            2 => {
                changed["request"]["members"][0]["placement_digest"] =
                    json!(hash(b"wrong proposal"))
            }
            3 => changed["request"]["scope_id"] = json!("production/wrong-cluster"),
            _ => changed["request_digest"] = json!(hash(b"wrong digest")),
        }
        // Re-sign nested grant and request so tuple and proposal checks, not merely
        // signature or request-hash verification, must reject the changed proof.
        if (1..=3).contains(&variant) {
            let request: Request = serde_json::from_value(changed["request"].clone()).unwrap();
            changed["request_digest"] = json!(hash(&serde_json::to_vec(&request).unwrap()));
            changed["genesis_grant"] = serde_json::to_value(sign("genesis", &serde_json::to_vec(&json!({"kind":"genesis_grant","grant_id":"grant-one","request_digest":changed["request_digest"]})).unwrap(),2)).unwrap();
            if variant == 2 {
                for member in changed["request"]["members"].as_array_mut().unwrap() {
                    member["placement_digest"] = json!(hash(b"wrong proposal"));
                }
                let request: Request = serde_json::from_value(changed["request"].clone()).unwrap();
                changed["request_digest"] = json!(hash(&serde_json::to_vec(&request).unwrap()));
                changed["genesis_grant"] = serde_json::to_value(sign("genesis", &serde_json::to_vec(&json!({"kind":"genesis_grant","grant_id":"grant-one","request_digest":changed["request_digest"]})).unwrap(),2)).unwrap();
            }
        }
        assert!(journal.accept("first", &acceptance(&changed, 1)).is_err(), "variant {variant}");
        assert!(journal.load_admitted_placement().is_err());
    }
    let forged = sign("authority", &serde_json::to_vec(&valid).unwrap(), 9);
    assert!(
        journal
            .accept(
                "first",
                &pb::PlacementAcceptRequest {
                    proposal_revision: 1,
                    proof: serde_json::to_vec(&forged).unwrap()
                }
            )
            .is_err()
    );
    assert!(journal.accept("different-operation", &acceptance(&valid, 1)).is_err());
    journal.accept("first", &acceptance(&valid, 1)).unwrap();
}

#[test]
fn successor_needs_one_fence_from_each_old_pair_and_exact_predecessor() {
    let mut fixture = tests::Fixture::new();
    fixture.request.proposal.as_mut().unwrap().regions.push(pb::PlacementRegionSeats {
        region: "SFO".into(),
        members: vec![MemberUlid::generate().to_text(), MemberUlid::generate().to_text()],
    });
    let journal = fixture.open();
    let first_proposal = stage(&journal, fixture.request.clone(), "first");
    let pins = trust(&first_proposal);
    journal.configure_transition_trust(pins.clone()).unwrap();
    let first = proof(&first_proposal, "first", None, &[]);
    journal.accept("first", &acceptance(&first, 1)).unwrap();
    let scope = *fixture.boundary.databases.read().unwrap().keys().next().unwrap();
    assert_eq!(journal.load_assignment(scope).unwrap().epoch, 1);
    let mut next = fixture.request.clone();
    next.expected_revision = 1;
    let next_proposal = next.proposal.as_mut().unwrap();
    next_proposal.generation = 2;
    next_proposal.epoch = 2;
    // Removing SFO from the new placement must not remove its old fencing duty.
    next_proposal.regions.retain(|region| region.region == "GRU");
    next_proposal.assignments.retain(|assignment| assignment.region == "GRU");
    let proposal = stage(&journal, next, "second");
    let missing = proof(&proposal, "second", Some(&first), &["GRU"]);
    assert!(journal.accept("second", &acceptance(&missing, 2)).is_err());
    let second = proof(&proposal, "second", Some(&first), &["GRU", "SFO"]);
    let mut wrong = second.clone();
    wrong["previous_proof_digest"] = json!(hash(b"different predecessor"));
    assert!(journal.accept("second", &acceptance(&wrong, 2)).is_err());
    let accepted = journal.accept("second", &acceptance(&second, 2)).unwrap();
    assert_eq!(accepted.epoch, 2);
    drop(journal);
    let mut advanced = fixture.boundary.clone();
    advanced.generation = 2;
    advanced.epoch = 2;
    let reopened =
        PlacementJournal::open(&fixture.root.join("proposals.sqlite"), advanced).unwrap();
    reopened.configure_transition_trust(pins).unwrap();
    assert_eq!(reopened.accepted().unwrap(), accepted);
    assert!(reopened.load_admitted_placement().is_ok());
}

#[test]
fn accepted_proposal_survives_new_local_database_registration() {
    let mut fixture = tests::Fixture::new();
    fixture.request.proposal.as_mut().unwrap().regions.push(pb::PlacementRegionSeats {
        region: "SFO".into(),
        members: vec![MemberUlid::generate().to_text(), MemberUlid::generate().to_text()],
    });
    let journal = fixture.open();
    let proposal = stage(&journal, fixture.request.clone(), "first");
    journal.configure_transition_trust(trust(&proposal)).unwrap();
    let first = proof(&proposal, "first", None, &[]);
    journal.accept("first", &acceptance(&first, 1)).unwrap();
    let original_scope = *fixture.boundary.databases.read().unwrap().keys().next().unwrap();

    let added_scope = DatabaseRouteScope::new(
        TenantUlid::generate(),
        ProjectUlid::generate(),
        DatabaseUlid::generate(),
    );
    journal
        .register_database(
            added_scope,
            "GRU",
            &std::collections::BTreeSet::from(["GRU".into(), "SFO".into()]),
        )
        .unwrap();
    assert_eq!(journal.load_assignment(original_scope).unwrap().epoch, 1);
    assert_eq!(journal.accepted_proposal().unwrap().revision, 1);

    let mut successor = fixture.request.clone();
    successor.expected_revision = 1;
    let successor_proposal = successor.proposal.as_mut().unwrap();
    successor_proposal.generation = 2;
    successor_proposal.epoch = 2;
    let sfo_members = successor_proposal.regions[1].members.clone();
    let seats = successor_proposal.regions[0].members.clone();
    successor_proposal.assignments.push(pb::PlacementDatabaseAssignment {
        tenant: added_scope.tenant().to_text(),
        project: added_scope.project().to_text(),
        database: added_scope.database().to_text(),
        region: "GRU".into(),
        writer_member: seats[0].clone(),
        follower_member: seats[1].clone(),
    });
    successor_proposal.assignments.push(pb::PlacementDatabaseAssignment {
        tenant: added_scope.tenant().to_text(),
        project: added_scope.project().to_text(),
        database: added_scope.database().to_text(),
        region: "SFO".into(),
        writer_member: sfo_members[0].clone(),
        follower_member: sfo_members[1].clone(),
    });
    let successor = stage(&journal, successor, "second");
    let second = proof(&successor, "second", Some(&first), &["GRU", "SFO"]);
    journal.accept("second", &acceptance(&second, 2)).unwrap();
    assert_eq!(journal.load_assignment(added_scope).unwrap().epoch, 2);

    drop(journal);
    let reopened = fixture.open();
    reopened.configure_transition_trust(trust(&proposal)).unwrap();
    assert_eq!(reopened.accepted().unwrap().epoch, 2);
    assert_eq!(reopened.load_assignment(original_scope).unwrap().epoch, 2);
    assert_eq!(reopened.load_assignment(added_scope).unwrap().epoch, 2);
}

#[test]
fn control_only_declaration_survives_restart_but_never_loads_wal() {
    let fixture = tests::Fixture::with_selected_and_local(
        std::collections::BTreeSet::from(["GRU".into(), "SFO".into()]),
        None,
    );
    let journal = fixture.open();
    let proposal = stage(&journal, fixture.request.clone(), "control");
    journal.configure_transition_trust(trust(&proposal)).unwrap();
    let proof = proof(&proposal, "control", None, &[]);
    journal.accept("control", &acceptance(&proof, 1)).unwrap();
    let scope = *fixture.boundary.databases.read().unwrap().keys().next().unwrap();
    assert!(journal.load_assignment(scope).is_err());
    drop(journal);

    let reopened = fixture.open();
    reopened.configure_transition_trust(trust(&proposal)).unwrap();
    assert_eq!(reopened.accepted().unwrap().epoch, 1);
    assert!(reopened.load_assignment(scope).is_err());
}

#[test]
fn control_only_declaration_requires_explicit_matching_promotion() {
    let fixture = tests::Fixture::with_selected_and_local(
        std::collections::BTreeSet::from(["GRU".into()]),
        None,
    );
    let journal = fixture.open();
    let proposal = stage(&journal, fixture.request.clone(), "promote");
    journal.configure_transition_trust(trust(&proposal)).unwrap();
    let proof = proof(&proposal, "promote", None, &[]);
    journal.accept("promote", &acceptance(&proof, 1)).unwrap();
    let scope = *fixture.boundary.databases.read().unwrap().keys().next().unwrap();
    assert_eq!(
        journal.register_database(
            scope,
            "GRU",
            &std::collections::BTreeSet::from(["GRU".into(), "SFO".into()]),
        ),
        Err(PlacementError::Conflict)
    );
    journal
        .register_database(scope, "GRU", &std::collections::BTreeSet::from(["GRU".into()]))
        .unwrap();
    assert_eq!(journal.load_assignment(scope).unwrap().epoch, 1);
}

#[test]
fn control_only_and_local_database_can_share_an_accepted_proposal() {
    let fixture = tests::Fixture::with_selected_and_local(
        std::collections::BTreeSet::from(["GRU".into()]),
        None,
    );
    let journal = fixture.open();
    let local_scope = DatabaseRouteScope::new(
        TenantUlid::generate(),
        ProjectUlid::generate(),
        DatabaseUlid::generate(),
    );
    journal
        .register_database(local_scope, "GRU", &std::collections::BTreeSet::from(["GRU".into()]))
        .unwrap();
    let mut request = fixture.request.clone();
    let seats = request.proposal.as_ref().unwrap().regions[0].members.clone();
    request.proposal.as_mut().unwrap().assignments.push(
        pb::PlacementDatabaseAssignment {
            tenant: local_scope.tenant().to_text(),
            project: local_scope.project().to_text(),
            database: local_scope.database().to_text(),
            region: "GRU".into(),
            writer_member: seats[0].clone(),
            follower_member: seats[1].clone(),
        },
    );
    let proposal = stage(&journal, request, "mixed-control");
    journal.configure_transition_trust(trust(&proposal)).unwrap();
    let proof = proof(&proposal, "mixed-control", None, &[]);
    journal.accept("mixed-control", &acceptance(&proof, 1)).unwrap();
    let control_scope = *fixture.boundary.databases.read().unwrap().keys().next().unwrap();
    assert!(journal.load_assignment(control_scope).is_err());
    assert_eq!(journal.load_assignment(local_scope).unwrap().epoch, 1);
}

#[test]
fn retained_digest_corruption_and_wrong_restart_pins_fail_closed() {
    let fixture = tests::Fixture::new();
    let journal = fixture.open();
    let proposal = stage(&journal, fixture.request.clone(), "first");
    journal.configure_transition_trust(trust(&proposal)).unwrap();
    let first = proof(&proposal, "first", None, &[]);
    journal.accept("first", &acceptance(&first, 1)).unwrap();
    drop(journal);
    let reopened = fixture.open();
    let wrong = TransitionTrust::new(
        BTreeMap::from([("authority".into(), key(9).public_key().as_ref().to_vec())]),
        BTreeMap::from([("genesis".into(), key(2).public_key().as_ref().to_vec())]),
        BTreeMap::new(),
    )
    .unwrap();
    assert!(reopened.configure_transition_trust(wrong).is_err());
    assert!(reopened.load_admitted_placement().is_err());
    reopened.configure_transition_trust(trust(&proposal)).unwrap();
    drop(reopened);
    let connection = Connection::open(fixture.root.join("proposals.sqlite")).unwrap();
    connection
        .execute("UPDATE accepted SET digest=?1", [hash(b"corrupt retained digest")])
        .unwrap();
    drop(connection);
    let reopened = fixture.open();
    assert!(reopened.configure_transition_trust(trust(&proposal)).is_err());
    assert!(reopened.load_admitted_placement().is_err());
}
