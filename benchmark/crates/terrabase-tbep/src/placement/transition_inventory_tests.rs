use super::*;

fn inventory(value: &mut Value, replacement: Option<&str>) {
    value["member_receipt_keys"] = Value::Object(
        value["request"]["members"]
            .as_array()
            .unwrap()
            .iter()
            .map(|member| {
                let identity = member["identity"].as_str().unwrap();
                let seed = if Some(identity) == replacement { 4 } else { 3 };
                (
                    identity.into(),
                    json!(
                        base64::engine::general_purpose::STANDARD
                            .encode(key(seed).public_key().as_ref())
                    ),
                )
            })
            .collect(),
    );
}

fn fence(value: &mut Value, identity: &str, seed: u8) {
    let receipt = serde_json::to_vec(&json!({
        "kind":"old_epoch_drained_fenced", "member_identity":identity,
        "scope_id":value["request"]["scope_id"],
        "prior_epoch":value["request"]["prior_epoch"],
        "prior_generation":value["prior_generation"],
        "request_digest":value["request_digest"], "frontier_digest":hash(b"durable frontier")
    }))
    .unwrap();
    value["receipt_digests"] = json!([hash(&receipt)]);
    value["drain_receipts"] = json!([sign(identity, &receipt, seed)]);
}

#[test]
fn new_member_fence_uses_prior_admitted_key_and_survives_restart() {
    let fixture = tests::Fixture::new();
    let journal = fixture.open();
    let first_proposal = stage(&journal, fixture.request.clone(), "first");
    let pins = trust(&first_proposal);
    journal.configure_transition_trust(pins.clone()).unwrap();
    let mut first = proof(&first_proposal, "first", None, &[]);
    inventory(&mut first, None);
    journal.accept("first", &acceptance(&first, 1)).unwrap();

    let mut next = fixture.request.clone();
    next.expected_revision = 1;
    let proposal = next.proposal.as_mut().unwrap();
    proposal.epoch = 2;
    proposal.generation = 2;
    let retired = proposal.assignments[0].follower_member.clone();
    let new_member = MemberUlid::generate().to_text();
    proposal.assignments[0].follower_member = new_member.clone();
    for member in &mut proposal.regions[0].members {
        if *member == retired {
            *member = new_member.clone();
        }
    }
    let second_proposal = stage(&journal, next.clone(), "second");
    let new_identity = MemberWorkloadIdentity::try_new(
        &second_proposal.environment,
        &second_proposal.cluster,
        &new_member,
    )
    .unwrap()
    .as_str()
    .to_owned();
    let mut second = proof(&second_proposal, "second", Some(&first), &["GRU"]);
    inventory(&mut second, Some(&new_identity));
    journal.accept("second", &acceptance(&second, 2)).unwrap();
    drop(journal);
    let journal = fixture.open();
    journal.configure_transition_trust(pins.clone()).unwrap();
    assert_eq!(journal.accepted().unwrap().epoch, 2);

    next.expected_revision = 2;
    next.proposal.as_mut().unwrap().epoch = 3;
    next.proposal.as_mut().unwrap().generation = 3;
    let third_proposal = stage(&journal, next, "third");
    let mut third = proof(&third_proposal, "third", Some(&second), &["GRU"]);
    inventory(&mut third, Some(&new_identity));
    fence(&mut third, &new_identity, 4);
    let mut forged = third.clone();
    forged["member_receipt_keys"][&new_identity] =
        json!(base64::engine::general_purpose::STANDARD.encode(key(5).public_key().as_ref()));
    fence(&mut forged, &new_identity, 5);
    assert!(journal.accept("third", &acceptance(&forged, 3)).is_err());
    let mut dropped = third.clone();
    dropped.as_object_mut().unwrap().remove("member_receipt_keys");
    assert!(journal.accept("third", &acceptance(&dropped, 3)).is_err());
    journal.accept("third", &acceptance(&third, 3)).unwrap();
    // Exact historical replay selects its own predecessor, not today's keys.
    assert_eq!(journal.accept("second", &acceptance(&second, 2)).unwrap().epoch, 2);
    drop(journal);
    let journal = fixture.open();
    journal.configure_transition_trust(pins).unwrap();
    assert_eq!(journal.accepted().unwrap().epoch, 3);
}

#[test]
fn signed_inventory_must_match_the_exact_accepted_member_set() {
    let fixture = tests::Fixture::new();
    let journal = fixture.open();
    let proposal = stage(&journal, fixture.request.clone(), "first");
    journal.configure_transition_trust(trust(&proposal)).unwrap();
    let mut valid = proof(&proposal, "first", None, &[]);
    inventory(&mut valid, None);
    let identity = valid["request"]["members"][0]["identity"].as_str().unwrap().to_owned();
    for case in 0..4 {
        let mut invalid = valid.clone();
        match case {
            0 => {
                invalid["member_receipt_keys"].as_object_mut().unwrap().remove(&identity);
            }
            1 => {
                invalid["member_receipt_keys"]["unadmitted-member"] =
                    invalid["member_receipt_keys"][&identity].clone();
            }
            2 => {
                invalid["member_receipt_keys"][&identity] = json!("AQ==");
            }
            _ => invalid["member_receipt_keys"] = json!({}),
        }
        assert!(journal.accept("first", &acceptance(&invalid, 1)).is_err());
    }
    journal.accept("first", &acceptance(&valid, 1)).unwrap();
}
