//! GAP-R03 tests: the typed replication observation operations (PB-5035).
//!
//! Every test drives a real durable member (or pair) through the same
//! deterministic lane idioms the R-1/R-2 suites use, then acquires the
//! bounded observable facts through the typed observation operations under
//! an explicit scope. Three properties are asserted throughout:
//!
//! - **Typed**: each operation returns its own observation type or the
//!   typed [`ObservationRefusal`], never an untyped dump.
//! - **Tenant-scoped**: an observation scoped to one tenant refuses typed
//!   when asked for another (the engine's `TenantScope` discipline,
//!   mirrored), and a matching claim still never sees another tenant's
//!   facts.
//! - **Content-free**: no rendered observation carries document bodies,
//!   field values, or collection payload text — the tests write canary
//!   documents and assert the canaries never appear.

#![allow(
    clippy::similar_names,
    reason = "fixtures bind per-member values whose canonical names differ by one character (m_a1/m_b1, dir_a1/dir_b1)"
)]

use crate::replication::observation::{
    ObservationRefusal, observe_conflicts, observe_cursor, observe_denials, observe_depositions,
    observe_fenced_history, observe_health,
};
use crate::replication::tests::{member_put, open_member};
use crate::replication::{
    AcknowledgementLevel, ConflictOutcome, DenialSeam, MemberId, ReplicationStatus, TopologyView,
    member::{MemberError, ReplicationMember, ReplicationWrite, synchronize},
    transport::{ReplicationFrame, ReplicationNetwork, StampedCommit},
};
use crate::storage::{PublishedCommit, PublishedWrite};
use crate::types::{
    CollectionName, Document, DocumentId, DocumentValue, LocalDurability, OperationId,
    StorageSequence, TenantId, TenantScope,
};

fn tenant(name: &str) -> TenantId {
    TenantId::try_new(name).expect("valid tenant")
}

fn member_id(name: &str) -> MemberId {
    MemberId::try_new(name).expect("valid member identifier")
}

fn operation(name: &str) -> OperationId {
    OperationId::try_new(name).expect("valid identifier")
}

fn collection(name: &str) -> CollectionName {
    CollectionName::try_new(name).expect("valid identifier")
}

fn document_id(name: &str) -> DocumentId {
    DocumentId::try_new(name).expect("valid identifier")
}

fn text_document(collection_name: &str, id: &str, text: &str) -> Document {
    Document::new(
        document_id(id),
        collection(collection_name),
        DocumentValue::String(text.to_owned()),
    )
}

fn put_request(range: &str, operation_name: &str, id: &str, text: &str) -> ReplicationWrite {
    ReplicationWrite::new(operation(operation_name), range, AcknowledgementLevel::Local)
        .expect("valid write request")
        .with_put(text_document("users", id, text))
}

/// The caller-supplied scope for one member, tenant, and engine instance.
fn scope(member: &ReplicationMember, tenant_name: &str) -> TenantScope {
    TenantScope::from_parts(tenant(tenant_name), member.engine().expect("engine open").identity())
}

/// Two Regions, one Member each, `owner` holding `range` at epoch 1.
fn two_regions_one_member_each(range: &str, owner: &str) -> TopologyView {
    TopologyView::new()
        .with_region("r-alpha")
        .expect("valid region")
        .with_region("r-beta")
        .expect("valid region")
        .with_member("m-a1", "r-alpha", "fd-alpha")
        .expect("valid member")
        .with_member("m-b1", "r-beta", "fd-beta")
        .expect("valid member")
        .with_owner(range, owner)
        .expect("valid owner")
        .validated()
        .expect("valid topology")
}

/// Divergent owner views of one range: each member seated as the owner.
fn divergent_owner_views(range: &str) -> (TopologyView, TopologyView) {
    (two_regions_one_member_each(range, "m-a1"), two_regions_one_member_each(range, "m-b1"))
}

/// The healed membership view: `m-b1` owns the range at the next epoch.
fn healed_view(range: &str) -> TopologyView {
    TopologyView::new()
        .with_region("r-alpha")
        .expect("valid region")
        .with_region("r-beta")
        .expect("valid region")
        .with_member("m-a1", "r-alpha", "fd-alpha")
        .expect("valid member")
        .with_member("m-b1", "r-beta", "fd-beta")
        .expect("valid member")
        .with_owner_at_epoch(range, "m-b1", 2)
        .expect("valid owner")
        .validated()
        .expect("valid topology")
}

#[test]
fn cross_tenant_observation_probes_refuse_typed_at_every_operation() {
    let topology = two_regions_one_member_each("main", "m-a1");
    let (dir, mut member) = open_member("observation-probe", "m-a1", &topology);
    member_put(&mut member, "tenant-a", "main", "op-1", "doc-1", "canary-a");

    let scope_a = scope(&member, "tenant-a");
    let scope_b = scope(&member, "tenant-b");
    let tenant_a = tenant("tenant-a");
    let tenant_b = tenant("tenant-b");

    // Scope tenant-a, asked for tenant-b: every operation refuses typed,
    // before any fact is read, mirroring the data plane's CrossTenant.
    let refusals: Vec<ObservationRefusal> = [
        observe_conflicts(&member, &scope_a, &tenant_b).err(),
        observe_fenced_history(&member, &scope_a, &tenant_b).err(),
        observe_depositions(&member, &scope_a, &tenant_b).err(),
        observe_denials(&member, &scope_a, &tenant_b).err(),
        observe_cursor(&member, &scope_a, &tenant_b).err(),
        observe_health(&member, &scope_a, &tenant_b).err(),
    ]
    .into_iter()
    .flatten()
    .collect();
    assert_eq!(
        refusals.len(),
        6,
        "every observation operation refuses the cross-tenant probe: {refusals:?}"
    );
    for refusal in &refusals {
        assert!(
            matches!(refusal, ObservationRefusal::CrossTenant { scope, attempted }
                if scope.as_str() == "tenant-a" && attempted.as_str() == "tenant-b"),
            "an observation scoped to tenant-a must refuse tenant-b typed: {refusal:?}"
        );
    }

    // The mirror: scope tenant-b, asked for tenant-a.
    let mirrored: Vec<ObservationRefusal> = [
        observe_conflicts(&member, &scope_b, &tenant_a).err(),
        observe_fenced_history(&member, &scope_b, &tenant_a).err(),
        observe_depositions(&member, &scope_b, &tenant_a).err(),
        observe_denials(&member, &scope_b, &tenant_a).err(),
        observe_cursor(&member, &scope_b, &tenant_a).err(),
        observe_health(&member, &scope_b, &tenant_a).err(),
    ]
    .into_iter()
    .flatten()
    .collect();
    assert_eq!(mirrored.len(), 6, "the mirrored probes refuse: {mirrored:?}");
    for refusal in &mirrored {
        assert!(
            matches!(refusal, ObservationRefusal::CrossTenant { scope, attempted }
                if scope.as_str() == "tenant-b" && attempted.as_str() == "tenant-a"),
            "an observation scoped to tenant-b must refuse tenant-a typed: {refusal:?}"
        );
    }

    member.close();
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn conflicts_observation_is_tenant_scoped_names_the_outcome_and_stays_content_free() {
    let (view_a, view_b) = divergent_owner_views("main");
    let (dir_a1, mut m_a1) = open_member("observe-conflict-a1", "m-a1", &view_a);
    let (dir_b1, mut m_b1) = open_member("observe-conflict-b1", "m-b1", &view_b);
    let mut network = ReplicationNetwork::new();

    let scope_a = scope(&m_a1, "tenant-a");
    m_a1.commit_write(&scope_a, put_request("main", "op-a", "doc-shared", "canary-value-a"))
        .expect("writer one commits");
    network.pump();
    m_b1.observe_instant(network.now());
    let scope_b = scope(&m_b1, "tenant-a");
    m_b1.commit_write(&scope_b, put_request("main", "op-b", "doc-shared", "canary-value-b"))
        .expect("writer two commits");

    let healed = healed_view("main");
    m_a1.apply_view(healed.clone()).expect("healed view applies");
    m_b1.apply_view(healed).expect("healed view applies");
    synchronize(&mut network, &mut m_a1, &scope_a, &mut m_b1, &scope_b, 4)
        .expect("repair reaches the promoted owner");

    let observation = observe_conflicts(&m_b1, &scope_b, &tenant("tenant-a"))
        .expect("the scope's own tenant observes its conflicts");
    assert_eq!(observation.member().as_str(), "m-b1");
    assert_eq!(observation.tenant().as_str(), "tenant-a");
    assert!(
        observation.records().iter().any(|record| matches!(
            record.outcome(),
            ConflictOutcome::Winner(ref member) if member.as_str() == "m-b1"
        )),
        "the observed records name the recorded policy outcome: {:?}",
        observation.records()
    );
    assert!(
        observation.records().iter().all(|record| record.tenant().as_str() == "tenant-a"),
        "only the claimed tenant's conflicts are observable"
    );

    // The same member observed under tenant-b's scope names no tenant-a
    // conflict: a matching claim never sees another tenant's facts.
    let scope_b_tenant_b = scope(&m_b1, "tenant-b");
    let other_tenant =
        observe_conflicts(&m_b1, &scope_b_tenant_b, &tenant("tenant-b")).expect("observes");
    assert!(
        other_tenant.records().is_empty(),
        "tenant-b's observation carries none of tenant-a's conflicts"
    );

    // Content-free: the canaries never appear in any rendered observation.
    let rendered = format!("{observation:?}{other_tenant:?}");
    for forbidden in ["canary-value-a", "canary-value-b", "doc-shared", "users"] {
        assert!(
            !rendered.contains(forbidden),
            "the conflicts observation carries document content ({forbidden}): {rendered}"
        );
    }

    m_a1.close();
    m_b1.close();
    std::fs::remove_dir_all(&dir_a1).ok();
    std::fs::remove_dir_all(&dir_b1).ok();
}

#[test]
fn fenced_history_and_depositions_are_observable_as_typed_operations() {
    let topology = two_regions_one_member_each("main", "m-a1");
    let (dir_a1, mut m_a1) = open_member("observe-fence-a1", "m-a1", &topology);
    let (dir_b1, mut m_b1) = open_member("observe-fence-b1", "m-b1", &topology);
    let a1 = member_id("m-a1");
    let b1 = member_id("m-b1");
    let mut network = ReplicationNetwork::new();

    let scope_a = scope(&m_a1, "tenant-a");
    m_a1.commit_write(&scope_a, put_request("main", "op-a", "doc-shared", "canary-value-a"))
        .expect("the old owner commits");
    let transitioned = healed_view("main");
    m_a1.apply_view(transitioned.clone()).expect("the old owner learns of the transition");
    m_b1.apply_view(transitioned).expect("the new owner is confirmed");

    // A below-epoch push from the deposed owner is fenced by the new
    // owner and retained as conflict history.
    let in_flight = PublishedCommit::new(
        tenant("tenant-a"),
        operation("op-a"),
        StorageSequence::new(1),
        LocalDurability::Synced,
        vec![PublishedWrite::Put {
            document: text_document("users", "doc-shared", "canary-value-a"),
        }],
    );
    network
        .send(
            &a1,
            &b1,
            ReplicationFrame::Push {
                source: a1.clone(),
                range: "main".to_owned(),
                epoch: 1,
                tenant: tenant("tenant-a"),
                commits: vec![StampedCommit::at_origin_clock(in_flight)],
            },
        )
        .expect("bounded link accepts the push frame");
    network.pump();
    let scope_b = scope(&m_b1, "tenant-a");
    let fenced = m_b1.apply_deliveries(&mut network, &scope_b);
    assert!(
        matches!(&fenced, Err(MemberError::FencedBatch { range, batch_epoch, highest_epoch })
            if range == "main" && *batch_epoch == 1 && *highest_epoch == 2),
        "the below-epoch push is fenced: {fenced:?}"
    );

    // Fenced-leader history: the new owner's observation names the range,
    // the epochs, and the deposed source.
    let fenced_observation =
        observe_fenced_history(&m_b1, &scope_b, &tenant("tenant-a")).expect("observes");
    assert_eq!(fenced_observation.member().as_str(), "m-b1");
    assert_eq!(fenced_observation.records().len(), 1, "the fenced batch is observable");
    assert_eq!(fenced_observation.records()[0].range(), "main");
    assert_eq!(fenced_observation.records()[0].batch_epoch(), 1);
    assert_eq!(fenced_observation.records()[0].highest_epoch(), 2);
    assert_eq!(fenced_observation.records()[0].source().as_str(), "m-a1");

    // Depositions: the old owner's observation names the transition that
    // deposed it.
    let depositions = observe_depositions(&m_a1, &scope_a, &tenant("tenant-a"))
        .expect("the old owner observes its own deposition");
    assert_eq!(depositions.member().as_str(), "m-a1");
    assert_eq!(depositions.records().len(), 1, "the deposition is observable");
    assert_eq!(depositions.records()[0].range(), "main");
    assert_eq!(depositions.records()[0].held_epoch(), 1);
    assert_eq!(depositions.records()[0].owner_epoch(), 2);
    assert_eq!(depositions.records()[0].new_owner().as_str(), "m-b1");

    let rendered = format!("{fenced_observation:?}{depositions:?}");
    for forbidden in ["canary-value-a", "doc-shared", "users"] {
        assert!(
            !rendered.contains(forbidden),
            "the fencing and deposition observations carry document content \
             ({forbidden}): {rendered}"
        );
    }

    m_a1.close();
    m_b1.close();
    std::fs::remove_dir_all(&dir_a1).ok();
    std::fs::remove_dir_all(&dir_b1).ok();
}

#[test]
fn cursor_observation_reports_the_claimed_tenants_position_and_refuses_typed_when_closed() {
    let topology = two_regions_one_member_each("main", "m-a1");
    let (dir, mut member) = open_member("observe-cursor", "m-a1", &topology);
    let first = member_put(&mut member, "tenant-a", "main", "op-1", "doc-a", "canary-a");
    let second = member_put(&mut member, "tenant-b", "main", "op-2", "doc-b", "canary-b");
    assert!(second.get() > first.get(), "the two tenants commit in sequence order");

    let scope_a = scope(&member, "tenant-a");
    let position_a = observe_cursor(&member, &scope_a, &tenant("tenant-a"))
        .expect("the scope's own tenant observes its cursor");
    assert_eq!(position_a.member().as_str(), "m-a1");
    assert_eq!(position_a.tenant().as_str(), "tenant-a");
    assert_eq!(position_a.position(), Some(first), "tenant-a's position is its own commit");

    let scope_b = scope(&member, "tenant-b");
    let position_b = observe_cursor(&member, &scope_b, &tenant("tenant-b"))
        .expect("tenant-b observes its cursor");
    assert_eq!(position_b.position(), Some(second), "tenant-b's position is its own commit");

    // A tenant with no commits observes no position — a fact, not an error.
    let scope_c = scope(&member, "tenant-c");
    let position_c =
        observe_cursor(&member, &scope_c, &tenant("tenant-c")).expect("an empty cursor observes");
    assert_eq!(position_c.position(), None, "no commit means no position");

    // A closed member's cursor is refused typed, never guessed.
    let scope_for_closed = scope(&member, "tenant-a");
    member.close();
    let closed = observe_cursor(&member, &scope_for_closed, &tenant("tenant-a"));
    assert!(
        matches!(closed, Err(ObservationRefusal::EngineClosed)),
        "a closed member's cursor observation refuses typed: {closed:?}"
    );

    let rendered = format!("{position_a:?}{position_b:?}{position_c:?}");
    for forbidden in ["canary-a", "canary-b", "doc-a", "doc-b", "users"] {
        assert!(
            !rendered.contains(forbidden),
            "the cursor observation carries document content ({forbidden}): {rendered}"
        );
    }

    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn health_observation_reports_the_member_status_and_never_a_convergence_fact() {
    let topology = two_regions_one_member_each("main", "m-a1");
    let (dir, mut member) = open_member("observe-health", "m-a1", &topology);

    let scope_a = scope(&member, "tenant-a");
    let fresh = observe_health(&member, &scope_a, &tenant("tenant-a"))
        .expect("health observes under the scope");
    assert_eq!(fresh.member().as_str(), "m-a1");
    assert_eq!(fresh.status(), ReplicationStatus::Assigned, "a seated, idle member");

    // A tenant-isolation denial degrades the member's health observation.
    let mut network = ReplicationNetwork::new();
    let b1 = member_id("m-b1");
    network
        .send(
            &b1,
            member.id(),
            ReplicationFrame::CatchUpRequest {
                requester: b1.clone(),
                tenant: tenant("tenant-a"),
                after: None,
                limit: 8,
            },
        )
        .expect("bounded link accepts the request frame");
    let scope_b = scope(&member, "tenant-b");
    let denied = member.serve_catch_up(&mut network, &scope_b);
    assert!(
        matches!(&denied, Err(MemberError::CrossTenant { scope, attempted })
            if scope.as_str() == "tenant-b" && attempted.as_str() == "tenant-a"),
        "the probe denial is recorded: {denied:?}"
    );
    let degraded = observe_health(&member, &scope_b, &tenant("tenant-b")).expect("health observes");
    assert_eq!(degraded.status(), ReplicationStatus::Degraded, "the denial degrades health");

    member.close();
    let unavailable = observe_health(&member, &scope_b, &tenant("tenant-b"))
        .expect("health never needs the engine");
    assert_eq!(unavailable.status(), ReplicationStatus::Unavailable);

    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn denials_observation_reports_the_isolation_log_of_the_claimed_tenants_scope() {
    let topology = two_regions_one_member_each("main", "m-a1");
    let (dir, mut member) = open_member("observe-denials", "m-a1", &topology);
    let mut network = ReplicationNetwork::new();

    // A hostile requester asks for tenant-a's history while the serving
    // scope names tenant-b: the denial is recorded against tenant-b's
    // scope, and only tenant-b's observation can see it.
    let b1 = member_id("m-b1");
    network
        .send(
            &b1,
            member.id(),
            ReplicationFrame::CatchUpRequest {
                requester: b1.clone(),
                tenant: tenant("tenant-a"),
                after: None,
                limit: 8,
            },
        )
        .expect("bounded link accepts the request frame");
    let scope_b = scope(&member, "tenant-b");
    let denied = member.serve_catch_up(&mut network, &scope_b);
    assert!(
        matches!(&denied, Err(MemberError::CrossTenant { scope, attempted })
            if scope.as_str() == "tenant-b" && attempted.as_str() == "tenant-a"),
        "the probe is denied: {denied:?}"
    );

    let denials = observe_denials(&member, &scope_b, &tenant("tenant-b"))
        .expect("the scope's own tenant observes its denials");
    assert_eq!(denials.member().as_str(), "m-a1");
    assert_eq!(denials.tenant().as_str(), "tenant-b");
    assert_eq!(denials.records().len(), 1, "the denial is observable");
    assert_eq!(denials.records()[0].scope_tenant().as_str(), "tenant-b");
    assert_eq!(denials.records()[0].attempted_tenant().as_str(), "tenant-a");
    assert_eq!(denials.records()[0].counterparty().as_str(), "m-b1");
    assert_eq!(
        denials.records()[0].seam(),
        DenialSeam::ServeRequest,
        "the denial names the seam it happened at"
    );

    // tenant-a's observation does not carry tenant-b's isolation log.
    let scope_a = scope(&member, "tenant-a");
    let foreign = observe_denials(&member, &scope_a, &tenant("tenant-a")).expect("observes");
    assert!(
        foreign.records().is_empty(),
        "the denials observation carries only the claimed tenant's scope"
    );

    member.close();
    std::fs::remove_dir_all(&dir).ok();
}
