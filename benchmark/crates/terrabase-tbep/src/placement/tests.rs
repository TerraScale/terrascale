use super::*;
use terrabase::identity::{ClusterUlid, DatabaseUlid, ProjectUlid, TenantUlid};

pub(crate) struct Fixture {
    pub root: std::path::PathBuf,
    pub boundary: PlacementBoundary,
    pub request: pb::PlacementStageRequest,
}

impl Fixture {
    pub fn new() -> Self {
        let cluster = ClusterUlid::generate().to_text();
        let member = MemberUlid::generate().to_text();
        let peer = MemberUlid::generate().to_text();
        let scope = DatabaseRouteScope::new(
            TenantUlid::generate(),
            ProjectUlid::generate(),
            DatabaseUlid::generate(),
        );
        let root = std::env::temp_dir().join(format!("terrabase-placement-{cluster}"));
        std::fs::create_dir(&root).expect("root");
        #[cfg(unix)]
        {
            use std::os::unix::fs::PermissionsExt as _;
            std::fs::set_permissions(&root, std::fs::Permissions::from_mode(0o700))
                .expect("private root");
        }
        let boundary =
            PlacementBoundary::new("production", &cluster, &member, 1, 1, [(scope, "GRU".into())])
                .expect("boundary");
        let request = pb::PlacementStageRequest {
            expected_revision: 0,
            proposal: Some(pb::PlacementProposal {
                environment: "production".into(),
                cluster,
                generation: 1,
                epoch: 1,
                regions: vec![pb::PlacementRegionSeats {
                    region: "GRU".into(),
                    members: vec![member.clone(), peer.clone()],
                }],
                assignments: vec![pb::PlacementDatabaseAssignment {
                    tenant: scope.tenant().to_text(),
                    project: scope.project().to_text(),
                    database: scope.database().to_text(),
                    region: "GRU".into(),
                    writer_member: member,
                    follower_member: peer,
                }],
            }),
        };
        Self { root, boundary, request }
    }

    pub fn open(&self) -> PlacementJournal {
        PlacementJournal::open(&self.root.join("proposals.sqlite"), self.boundary.clone())
            .expect("journal")
    }
}

impl Drop for Fixture {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.root);
    }
}

#[test]
fn restart_retains_pending_proposal_and_exact_retry_after_successor() {
    let fixture = Fixture::new();
    let journal = fixture.open();
    assert_eq!(journal.latest(), Err(PlacementError::NotFound));
    let first = journal
        .stage("spiffe://test/orchestrator", "operation-one", fixture.request.clone())
        .expect("first durable proposal");
    assert_eq!(first.status, pb::PlacementProposalStatus::Pending as i32);
    let mut next = fixture.request.clone();
    next.expected_revision = first.revision;
    next.proposal.as_mut().unwrap().generation = 2;
    next.proposal.as_mut().unwrap().epoch = 2;
    let second = journal
        .stage("spiffe://test/orchestrator", "operation-two", next)
        .expect("successor intent");
    drop(journal);
    let reopened = fixture.open();
    assert_eq!(reopened.latest().expect("retained head"), second);
    let mut reordered = fixture.request.clone();
    reordered.proposal.as_mut().unwrap().regions[0].members.reverse();
    assert_eq!(
        reopened
            .stage("spiffe://test/orchestrator", "operation-one", reordered)
            .expect("exact retained receipt"),
        first
    );
    assert_eq!(
        reopened.stage("spiffe://test/other", "operation-one", fixture.request.clone()),
        Err(PlacementError::RetryMismatch)
    );
}

#[test]
fn stale_revision_conflicting_epoch_and_changed_operation_never_replace_head() {
    let fixture = Fixture::new();
    let journal = fixture.open();
    let first = journal.stage("principal", "operation", fixture.request.clone()).unwrap();
    let mut changed = fixture.request.clone();
    changed.expected_revision = 1;
    changed.proposal.as_mut().unwrap().generation = 2;
    assert_eq!(
        journal.stage("principal", "changed", changed.clone()),
        Err(PlacementError::Conflict)
    );
    assert_eq!(
        journal.stage("principal", "operation", changed.clone()),
        Err(PlacementError::RetryMismatch)
    );
    changed.proposal.as_mut().unwrap().epoch = 2;
    changed.expected_revision = 0;
    assert_eq!(journal.stage("principal", "changed", changed), Err(PlacementError::Conflict));
    assert_eq!(journal.latest().unwrap(), first);
}

#[test]
fn invalid_roles_residency_scope_and_environment_are_rejected_before_write() {
    let fixture = Fixture::new();
    let journal = fixture.open();
    for variant in 0..6 {
        let mut request = fixture.request.clone();
        let proposal = request.proposal.as_mut().unwrap();
        match variant {
            0 => proposal.environment = "different".into(),
            1 => proposal.assignments[0].region = "FRA".into(),
            2 => {
                proposal.assignments[0].writer_member =
                    proposal.assignments[0].follower_member.clone()
            }
            3 => proposal.assignments[0].database = DatabaseUlid::generate().to_text(),
            4 => proposal.regions[0].members[1] = proposal.regions[0].members[0].clone(),
            _ => proposal.assignments.clear(),
        }
        assert_eq!(journal.stage("principal", "operation", request), Err(PlacementError::Invalid));
    }
    assert_eq!(journal.latest(), Err(PlacementError::NotFound));
}

#[test]
fn independent_connections_serialize_expected_revision() {
    let fixture = Fixture::new();
    let left = fixture.open();
    let right = fixture.open();
    let barrier = std::sync::Arc::new(std::sync::Barrier::new(2));
    let request = fixture.request.clone();
    let thread_barrier = std::sync::Arc::clone(&barrier);
    let worker = std::thread::spawn(move || {
        thread_barrier.wait();
        left.stage("principal", "left", request)
    });
    barrier.wait();
    let second = right.stage("principal", "right", fixture.request.clone());
    let first = worker.join().unwrap();
    assert_eq!(usize::from(first.is_ok()) + usize::from(second.is_ok()), 1);
    assert!(first == Err(PlacementError::Conflict) || second == Err(PlacementError::Conflict));
    assert_eq!(right.latest().unwrap().revision, 1);
}

#[test]
fn different_identity_and_corrupt_payload_fail_on_reopen() {
    let fixture = Fixture::new();
    let journal = fixture.open();
    journal.stage("principal", "operation", fixture.request.clone()).unwrap();
    drop(journal);
    let mut wrong = fixture.boundary.clone();
    wrong.member = MemberUlid::generate().to_text();
    assert!(matches!(
        PlacementJournal::open(&fixture.root.join("proposals.sqlite"), wrong),
        Err(PlacementError::IdentityMismatch)
    ));
    let connection = Connection::open(fixture.root.join("proposals.sqlite")).unwrap();
    connection.execute("UPDATE proposals SET proposal=?1", [vec![0_u8]]).unwrap();
    drop(connection);
    assert!(matches!(
        PlacementJournal::open(&fixture.root.join("proposals.sqlite"), fixture.boundary.clone()),
        Err(PlacementError::Storage)
    ));
}

#[cfg(unix)]
#[test]
fn symlink_journal_and_public_parent_are_rejected() {
    use std::os::unix::fs::{PermissionsExt as _, symlink};
    let fixture = Fixture::new();
    std::fs::write(fixture.root.join("target"), []).unwrap();
    symlink(fixture.root.join("target"), fixture.root.join("proposals.sqlite")).unwrap();
    assert!(matches!(
        PlacementJournal::open(&fixture.root.join("proposals.sqlite"), fixture.boundary.clone()),
        Err(PlacementError::Storage)
    ));
    std::fs::remove_file(fixture.root.join("proposals.sqlite")).unwrap();
    std::fs::set_permissions(&fixture.root, std::fs::Permissions::from_mode(0o755)).unwrap();
    assert!(matches!(
        PlacementJournal::open(&fixture.root.join("proposals.sqlite"), fixture.boundary.clone()),
        Err(PlacementError::Storage)
    ));
}
