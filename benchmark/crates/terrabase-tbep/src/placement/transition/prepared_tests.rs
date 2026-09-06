use super::*;
use ring::signature::Ed25519KeyPair;
use serde_json::{Value, json};
use terrabase::identity::MemberUlid;

fn authority_signed(payload: &[u8]) -> Signed {
    let key = Ed25519KeyPair::from_seed_unchecked(&[1; 32]).unwrap();
    let mut message = DOMAIN.to_vec();
    message.extend_from_slice(payload);
    Signed {
        key_id: "authority".into(),
        payload: base64::engine::general_purpose::STANDARD.encode(payload),
        signature: base64::engine::general_purpose::STANDARD.encode(key.sign(&message)),
    }
}

fn build_prior(selected: &[&str]) -> (TransitionTrust, VerifiedProof, Request) {
    let fixture = crate::placement::tests::Fixture::with_selected(
        selected.iter().map(|region| (*region).to_owned()).collect(),
    );
    let journal = fixture.open();
    let mut stage_request = fixture.request.clone();
    if selected.contains(&"SFO") {
        let proposal = stage_request.proposal.as_mut().unwrap();
        let members = vec![MemberUlid::generate().to_text(), MemberUlid::generate().to_text()];
        let mut assignment = proposal.assignments[0].clone();
        assignment.region = "SFO".into();
        assignment.writer_member = members[0].clone();
        assignment.follower_member = members[1].clone();
        proposal.assignments.push(assignment);
        proposal.regions.push(crate::proto::terrabase::v1::PlacementRegionSeats {
            region: "SFO".into(),
            members,
        });
    }
    let proposal = journal
        .stage("authenticated-orchestrator", "prior", stage_request)
        .unwrap()
        .proposal
        .unwrap();
    let trust = crate::placement::transition_tests::trust(&proposal);
    let accepted = crate::placement::transition_tests::proof(&proposal, "prior", None, &[]);
    let signed = crate::placement::transition_tests::acceptance(&accepted, 1).proof;
    let verified = trust.decode(&signed).unwrap();
    let request = serde_json::from_value(accepted["request"].clone()).unwrap();
    (trust, verified, request)
}

fn prepared(
    trust: &TransitionTrust,
    prior: &VerifiedProof,
    mut request: Request,
    authority_epoch: u64,
    journal_revision: u64,
) -> Result<VerifiedPreparedTransition, PlacementError> {
    request.prior_epoch = prior.proof.request.epoch;
    request.generation = prior.proof.request.generation + 1;
    request.epoch = request.prior_epoch + 1;
    let request_digest = hash(&serde_json::to_vec(&request).unwrap());
    let payload = serde_json::to_vec(&json!({
        "schema_version": 1,
        "kind": "prepared_epoch",
        "request": request,
        "request_digest": request_digest,
        "prior_generation": prior.proof.request.generation,
        "previous_proof_digest": prior.digest,
        "authority_epoch": authority_epoch,
        "lease_id": "lease-next",
        "journal_revision": journal_revision,
    }))
    .unwrap();
    let encoded = serde_json::to_vec(&authority_signed(&payload)).unwrap();
    trust.decode_prepared(&encoded, prior)
}

#[test]
fn transition_receipt_signer_binds_member_and_frontier() {
    let (trust, prior, request) = build_prior(&["GRU"]);
    let prepared = prepared(&trust, &prior, request, 2, 2).unwrap();
    let der = Ed25519KeyPair::generate_pkcs8(&ring::rand::SystemRandom::new()).unwrap();
    let signer = TransitionReceiptSigner::from_pkcs8("member-1".into(), der.as_ref()).unwrap();
    let receipt = signer
        .sign_old_epoch_fenced(&prepared, "member-1", &"a".repeat(64))
        .unwrap();
    let envelope: Signed = serde_json::from_slice(&receipt).unwrap();
    let payload = verify(&envelope, &BTreeMap::from([(
        "member-1".into(),
        Ed25519KeyPair::from_pkcs8(der.as_ref())
            .unwrap()
            .public_key()
            .as_ref()
            .to_vec(),
    )]))
    .unwrap();
    let decoded: FenceReceipt = serde_json::from_slice(&payload).unwrap();
    assert_eq!(decoded.request_digest, prepared.request_digest());
    assert!(signer
        .sign_old_epoch_fenced(&prepared, "other", &"a".repeat(64))
        .is_err());
    assert!(signer.sign_old_epoch_fenced(&prepared, "member-1", "bad").is_err());
}

#[test]
fn accepts_valid_topology_growth_and_shrink() {
    let (trust, prior, mut request) = build_prior(&["GRU"]);
    request.members.extend([
        TransitionMember {
            identity: "zz-sfo-a".into(),
            region: "SFO".into(),
            placement_digest: "a".repeat(64),
        },
        TransitionMember {
            identity: "zz-sfo-b".into(),
            region: "SFO".into(),
            placement_digest: "b".repeat(64),
        },
    ]);
    let growth = prepared(&trust, &prior, request, 2, 2).unwrap();
    assert_eq!(growth.prior_epoch(), 1);
    assert_eq!(growth.epoch(), 2);

    let (trust, prior, mut request) = build_prior(&["GRU", "SFO"]);
    request.members.retain(|member| member.region == "GRU");
    let shrink = prepared(&trust, &prior, request, 2, 2).unwrap();
    assert_eq!(shrink.generation(), 2);
}

#[test]
fn rejects_signature_unknown_fields_and_member_ordering() {
    let (trust, prior, mut request) = build_prior(&["GRU"]);
    request.prior_epoch = prior.proof.request.epoch;
    request.epoch = request.prior_epoch + 1;
    request.generation = prior.proof.request.generation + 1;
    let request_digest = hash(&serde_json::to_vec(&request).unwrap());
    assert!(prepared(&trust, &prior, request.clone(), 2, 2).is_ok());
    let mut value = json!({
        "schema_version": 1, "kind": "prepared_epoch", "request": request,
        "request_digest": request_digest.clone(), "prior_generation": 1,
        "previous_proof_digest": prior.digest, "authority_epoch": 2,
        "lease_id": "lease-next", "journal_revision": 2,
    });
    value["unexpected"] = Value::Bool(true);
    let payload = serde_json::to_vec(&value).unwrap();
    let encoded = serde_json::to_vec(&authority_signed(&payload)).unwrap();
    assert!(trust.decode_prepared(&encoded, &prior).is_err());

    value.as_object_mut().unwrap().remove("unexpected");
    value["request_digest"] = Value::String("0".repeat(64));
    let encoded =
        serde_json::to_vec(&authority_signed(&serde_json::to_vec(&value).unwrap())).unwrap();
    assert!(trust.decode_prepared(&encoded, &prior).is_err());

    value["request_digest"] = Value::String(request_digest);
    let mut tampered = authority_signed(&serde_json::to_vec(&value).unwrap());
    tampered.signature = base64::engine::general_purpose::STANDARD.encode([0; 64]);
    let encoded = serde_json::to_vec(&tampered).unwrap();
    assert!(trust.decode_prepared(&encoded, &prior).is_err());
}

#[test]
fn rejects_stale_authority_revision_and_duplicate_identity() {
    let (trust, prior, request) = build_prior(&["GRU"]);
    assert!(prepared(&trust, &prior, request.clone(), 0, 2).is_err());
    assert!(prepared(&trust, &prior, request.clone(), 2, 1).is_err());
    let mut duplicate = request.clone();
    duplicate.members[1].identity = duplicate.members[0].identity.clone();
    assert!(prepared(&trust, &prior, duplicate, 2, 2).is_err());
    let mut unordered = request;
    unordered.members.swap(0, 1);
    assert!(prepared(&trust, &prior, unordered, 2, 2).is_err());
}
