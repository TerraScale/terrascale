use super::*;
use crate::placement::tests::Fixture;

pub(crate) fn grant(
    fixture: &Fixture,
    identity: &pb::AuthStorageIdentity,
) -> pb::AuthGroupBootstrapRequest {
    let proposal = fixture.request.proposal.as_ref().unwrap();
    let assignment = &proposal.assignments[0];
    let mut members = proposal.regions[0].members.clone();
    members.sort();
    pb::AuthGroupBootstrapRequest {
        operation_id: process_generator().generate().to_text(),
        grant: Some(pb::AuthGroupBootstrapGrant {
            environment: proposal.environment.clone(),
            cluster: proposal.cluster.clone(),
            group_id: process_generator().generate().to_text(),
            tenant: assignment.tenant.clone(),
            project: assignment.project.clone(),
            database: assignment.database.clone(),
            generation: 1,
            regions: vec!["GRU".into()],
            authority_revision: 7,
            genesis_node: 1,
            voters: members
                .into_iter()
                .enumerate()
                .map(|(index, member)| pb::AuthGroupVoter {
                    node_id: index as u64 + 1,
                    spiffe_uri: MemberWorkloadIdentity::try_new(
                        &proposal.environment,
                        &proposal.cluster,
                        &member,
                    )
                    .unwrap()
                    .as_str()
                    .into(),
                    storage_incarnation: if member == identity.member {
                        identity.storage_incarnation.clone()
                    } else {
                        process_generator().generate().to_text()
                    },
                    member,
                    region: "GRU".into(),
                })
                .collect(),
        }),
    }
}

#[test]
fn grant_replay_is_immutable_and_state_creation_is_not_reissued_after_restart() {
    let fixture = Fixture::new();
    let journal = fixture.open_auth();
    let identity = journal.identity().unwrap();
    let request = grant(&fixture, &identity);
    let receipt = journal.bootstrap("gru-control", request.clone()).unwrap();
    assert_eq!(receipt.grant, request.grant);
    assert_eq!(receipt.status, pb::AuthGroupBootstrapStatus::Persisted as i32);
    let group = &receipt.group_id;
    assert!(journal.reserve_state_creation(group).unwrap());
    assert!(!journal.reserve_state_creation(group).unwrap());
    drop(journal);
    let reopened = fixture.open_auth();
    assert_eq!(reopened.identity().unwrap(), identity);
    assert_eq!(reopened.bootstrap("gru-control", request.clone()).unwrap(), receipt);
    assert!(!reopened.reserve_state_creation(group).unwrap());
    assert_eq!(
        reopened.bootstrap("different-control", request.clone()),
        Err(PlacementError::RetryMismatch)
    );
    let mut replacement = request;
    replacement.operation_id = process_generator().generate().to_text();
    replacement.grant.as_mut().unwrap().group_id = process_generator().generate().to_text();
    assert_eq!(reopened.bootstrap("gru-control", replacement), Err(PlacementError::RetryMismatch));
}

#[test]
fn lost_control_disk_cannot_replay_genesis_for_its_previous_incarnation() {
    let fixture = Fixture::new();
    let journal = fixture.open_auth();
    let identity = journal.identity().unwrap();
    let request = grant(&fixture, &identity);
    journal.bootstrap("gru-control", request.clone()).unwrap();
    drop(journal);
    std::fs::remove_file(fixture.root.join("auth-bootstrap.sqlite")).unwrap();
    let new_disk = fixture.open_auth();
    assert_ne!(new_disk.identity().unwrap().storage_incarnation, identity.storage_incarnation);
    assert_eq!(new_disk.bootstrap("gru-control", request), Err(PlacementError::IdentityMismatch));
}

#[test]
fn grant_rejects_foreign_region_missing_voter_and_changed_local_identity() {
    let fixture = Fixture::new();
    let journal = fixture.open_auth();
    let identity = journal.identity().unwrap();
    let valid = grant(&fixture, &identity);
    let mut malformed = valid.clone();
    malformed.grant.as_mut().unwrap().voters[0].region = "FRA".into();
    assert_eq!(journal.bootstrap("gru-control", malformed), Err(PlacementError::Invalid));
    let mut malformed = valid.clone();
    malformed.grant.as_mut().unwrap().voters.pop();
    assert_eq!(journal.bootstrap("gru-control", malformed), Err(PlacementError::Invalid));
    let mut malformed = valid;
    malformed
        .grant
        .as_mut()
        .unwrap()
        .voters
        .iter_mut()
        .find(|voter| voter.member == identity.member)
        .unwrap()
        .storage_incarnation = process_generator().generate().to_text();
    assert_eq!(journal.bootstrap("gru-control", malformed), Err(PlacementError::IdentityMismatch));
}
