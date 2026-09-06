use super::*;
use rusqlite::Connection;
use terrabase::identity::{DatabaseUlid, ProjectUlid, TenantUlid};
use terrabase_cluster::wal_route::DurableWalAssignmentSource as _;

fn unknown_scope() -> DatabaseRouteScope {
    DatabaseRouteScope::new(
        TenantUlid::generate(),
        ProjectUlid::generate(),
        DatabaseUlid::generate(),
    )
}

fn with_control_declaration(
    mut request: pb::PlacementControlServiceStageRequest,
    scope: DatabaseRouteScope,
) -> pb::PlacementControlServiceStageRequest {
    let proposal = request.proposal.as_mut().unwrap();
    let seats = proposal.regions[0].members.clone();
    proposal.assignments.push(pb::PlacementDatabaseAssignment {
        tenant: scope.tenant().to_text(),
        project: scope.project().to_text(),
        database: scope.database().to_text(),
        region: "GRU".into(),
        writer_member: seats[0].clone(),
        follower_member: seats[1].clone(),
    });
    request.declarations.push(pb::PlacementDatabaseDeclaration {
        tenant: scope.tenant().to_text(),
        project: scope.project().to_text(),
        database: scope.database().to_text(),
        selected_regions: vec!["GRU".into()],
    });
    request
}

fn successor(mut request: pb::PlacementControlServiceStageRequest) -> pb::PlacementControlServiceStageRequest {
    request.expected_revision = 1;
    let proposal = request.proposal.as_mut().unwrap();
    proposal.generation = 2;
    proposal.epoch = 2;
    request.declarations.clear();
    request
}

#[test]
fn stale_cas_does_not_persist_control_declaration_and_retry_identity_is_immutable() {
    let fixture = tests::Fixture::new();
    let scope = unknown_scope();
    let request = with_control_declaration(fixture.request.clone(), scope);
    let journal = fixture.open();

    let mut stale = request.clone();
    stale.expected_revision = 1;
    assert_eq!(journal.stage("orchestrator", "stale", stale), Err(PlacementError::Conflict));
    drop(journal);

    // Reopening proves the failed CAS did not leave a declaration behind.
    let journal = fixture.open();
    let connection = Connection::open(fixture.root.join("proposals.sqlite")).unwrap();
    assert!(!super::read_declarations(&connection).unwrap().contains_key(&scope));
    drop(connection);
    let response = journal.stage("orchestrator", "first", request.clone()).unwrap();
    assert_eq!(response.revision, 1);

    // Expanding the durable catalog with a different scope must not change the
    // retained result for operation A.
    let scope_b = unknown_scope();
    journal
        .register_database(scope_b, "GRU", &std::collections::BTreeSet::from(["GRU".into()]))
        .unwrap();
    assert_eq!(journal.stage("orchestrator", "first", request.clone()).unwrap(), response);

    // Changing declaration bytes under the retained operation identity cannot mutate the catalog.
    let mut changed = request;
    changed.declarations[0].selected_regions = vec!["GRU".into(), "SFO".into()];
    assert!(journal.stage("orchestrator", "first", changed).is_err());
    assert!(
        journal
            .register_database(scope, "GRU", &std::collections::BTreeSet::from(["GRU".into()]))
            .is_ok()
    );
}

#[test]
fn control_only_declaration_survives_restart_and_never_grants_wal_assignment() {
    let fixture = tests::Fixture::with_selected_and_local(
        std::collections::BTreeSet::from(["GRU".into()]),
        None,
    );
    let scope = unknown_scope();
    let journal = fixture.open();
    let request = with_control_declaration(fixture.request.clone(), scope);
    let proposal = journal.stage("orchestrator", "control", request).unwrap().proposal.unwrap();
    journal.configure_transition_trust(super::transition_tests::trust(&proposal)).unwrap();
    let first = super::transition_tests::proof(&proposal, "control", None, &[]);
    journal.accept("control", &super::transition_tests::acceptance(&first, 1)).unwrap();
    assert!(journal.load_assignment(scope).is_err());
    drop(journal);

    let reopened = fixture.open();
    reopened.configure_transition_trust(super::transition_tests::trust(&proposal)).unwrap();
    assert_eq!(reopened.accepted().unwrap().epoch, 1);
    assert!(reopened.load_assignment(scope).is_err());
    reopened
        .register_database(scope, "GRU", &std::collections::BTreeSet::from(["GRU".into()]))
        .unwrap();
    let assignment = reopened.load_assignment(scope).unwrap();
    assert_eq!(assignment.generation, 1);
    assert_eq!(assignment.epoch, 1);
}

#[test]
fn independent_journal_connection_preserves_local_promotion_when_staging_successor() {
    let fixture = tests::Fixture::with_selected_and_local(
        std::collections::BTreeSet::from(["GRU".into()]),
        None,
    );
    let scope = unknown_scope();
    let first = with_control_declaration(fixture.request.clone(), scope);
    let base_scope = *fixture.boundary.databases.read().unwrap().keys().next().unwrap();
    let selected = std::collections::BTreeSet::from(["GRU".into()]);
    let boundary_a = PlacementBoundary::new(
        &fixture.boundary.environment,
        &fixture.boundary.cluster,
        &fixture.boundary.member,
        fixture.boundary.generation,
        fixture.boundary.epoch,
        [(base_scope, None, selected.clone())],
    )
    .unwrap();
    let boundary_b = PlacementBoundary::new(
        &fixture.boundary.environment,
        &fixture.boundary.cluster,
        &fixture.boundary.member,
        fixture.boundary.generation,
        fixture.boundary.epoch,
        [(base_scope, None, selected)],
    )
    .unwrap();
    let path = fixture.root.join("proposals.sqlite");
    let journal = PlacementJournal::open(&path, boundary_a).unwrap();
    let promoted = PlacementJournal::open(&path, boundary_b).unwrap();
    let first_proposal = journal.stage("orchestrator", "first", first).unwrap().proposal.unwrap();
    journal.configure_transition_trust(super::transition_tests::trust(&first_proposal)).unwrap();
    let first_proof = super::transition_tests::proof(&first_proposal, "first", None, &[]);
    journal.accept("first", &super::transition_tests::acceptance(&first_proof, 1)).unwrap();

    // A separate SQLite connection performs the explicit local promotion.
    promoted
        .register_database(scope, "GRU", &std::collections::BTreeSet::from(["GRU".into()]))
        .unwrap();

    // The first connection must reread durable declarations before accepting the successor.
    let next = successor({
        let mut request = fixture.request.clone();
        let proposal = request.proposal.as_mut().unwrap();
        let seats = proposal.regions[0].members.clone();
        proposal.generation = 2;
        proposal.epoch = 2;
        proposal.assignments.push(pb::PlacementDatabaseAssignment {
            tenant: scope.tenant().to_text(),
            project: scope.project().to_text(),
            database: scope.database().to_text(),
            region: "GRU".into(),
            writer_member: seats[0].clone(),
            follower_member: seats[1].clone(),
        });
        request
    });
    let next_proposal = journal.stage("orchestrator", "successor", next).unwrap().proposal.unwrap();
    let next_proof =
        super::transition_tests::proof(&next_proposal, "successor", Some(&first_proof), &["GRU"]);
    journal.accept("successor", &super::transition_tests::acceptance(&next_proof, 2)).unwrap();

    let reopened_a = fixture.open();
    reopened_a.configure_transition_trust(super::transition_tests::trust(&next_proposal)).unwrap();
    assert_eq!(reopened_a.accepted().unwrap().epoch, 2);
    let assignment_a = reopened_a.load_assignment(scope).unwrap();
    assert_eq!(assignment_a.generation, 2);
    assert_eq!(assignment_a.epoch, 2);

    drop(promoted);
    let reopened_b = fixture.open();
    reopened_b.configure_transition_trust(super::transition_tests::trust(&next_proposal)).unwrap();
    assert_eq!(reopened_b.accepted().unwrap().epoch, 2);
    let assignment_b = reopened_b.load_assignment(scope).unwrap();
    assert_eq!(assignment_b.generation, 2);
    assert_eq!(assignment_b.epoch, 2);
}
