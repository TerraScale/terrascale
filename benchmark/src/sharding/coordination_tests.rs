//! SH-05 coordination seam tests: shard-loss failover, cluster-scoped
//! transactions, and per-shard recovery reporting (PB-5040/5041/5048).
//!
//! Every test crosses the `sharding::coordination` seam from inside the
//! crate (engine construction needs the crate-private `TenantScope`), over
//! real durable Engines in temporary directories. Nothing here makes a
//! timing, production-topology, or performance claim.

use crate::engine::{DurableResolution, Engine, EngineConfig};
use crate::replication::{MemberId, RegionId, TopologyView};
use crate::sharding::coordination::{
    ATOMIC_WITHIN_ONE_SHARD, ClusterOutcome, ClusterStage, ClusterTransaction,
    ClusterTransactionError, EngineWriteServing, FailoverDecision, FailoverError, MemberRecovery,
    RecoveryError, ShardCommit, ShardLoss, ShardRecoveryReport, ShardRefusal, ShardWriteError,
    ShardWriteSet,
};
use crate::sharding::{
    BUCKET_SPACE, BucketRange, BucketRangeTable, RoutingRequest, ShardId, ShardTopology,
    ShardingError,
};
use crate::types::{
    CollectionName, Document, DocumentId, DocumentValue, LocalDurability, OperationId, TenantId,
    TenantScope,
};

const HALF_SPACE: u32 = BUCKET_SPACE / 2;

const LANE_ROSTER: [(&str, &str); 6] = [
    ("m-a1", "r-alpha"),
    ("m-a2", "r-alpha"),
    ("m-b1", "r-beta"),
    ("m-b2", "r-beta"),
    ("m-g1", "r-gamma"),
    ("m-g2", "r-gamma"),
];

fn sharded_view() -> TopologyView {
    let mut view = TopologyView::new();
    for region in ["r-alpha", "r-beta", "r-gamma"] {
        view = view.with_region(region).expect("valid region");
    }
    for (member, region) in LANE_ROSTER {
        view = view.with_member(member, region, &format!("fd-{region}")).expect("valid member");
    }
    view.validated().expect("servable view")
}

fn sharded_topology() -> ShardTopology {
    let view = sharded_view()
        .with_owner("shard/1", "m-a1")
        .expect("valid owner")
        .with_owner("shard/2", "m-b1")
        .expect("valid owner");
    let table = BucketRangeTable::try_new(&[
        BucketRange::try_new(0, HALF_SPACE, ShardId::from_number(1)).expect("valid range"),
        BucketRange::try_new(HALF_SPACE, BUCKET_SPACE, ShardId::from_number(2))
            .expect("valid range"),
    ])
    .expect("valid table");
    ShardTopology::try_new(table, view).expect("seats")
}

/// A full view with `shard/1` owned by `owner` at `epoch` and `shard/2`
/// left exactly as the seated topology holds it.
fn view_with_shard1_owner(owner: &str, epoch: u64) -> TopologyView {
    sharded_view()
        .with_owner_at_epoch("shard/1", owner, epoch)
        .expect("valid owner")
        .with_owner_at_epoch("shard/2", "m-b1", 1)
        .expect("valid owner")
}

fn member(name: &str) -> MemberId {
    MemberId::try_new(name).expect("valid member")
}

fn tenant() -> TenantId {
    TenantId::try_new("tenant-lane").expect("valid tenant")
}

fn document(name: &str) -> Document {
    Document::new(
        DocumentId::try_new(name).expect("valid identity"),
        CollectionName::try_new("orders").expect("valid collection"),
        DocumentValue::String(format!("payload-{name}")),
    )
}

/// Finds one document that routes to the wanted shard under identity keys.
fn document_for_shard(topology: &ShardTopology, shard: ShardId) -> Document {
    let tenant = tenant();
    for index in 0..200u32 {
        let candidate = document(&format!("doc-{index}"));
        let request = RoutingRequest::of_document(&tenant, &candidate);
        if let Ok(placement) = topology.route(&request)
            && placement.shard() == shard
        {
            return candidate;
        }
    }
    panic!("no document found for shard {shard:?}");
}

fn location(tag: &str) -> std::path::PathBuf {
    std::env::temp_dir().join(format!("terrabase-sh05-{tag}-{}", std::process::id()))
}

fn open_engine(tag: &str, name: &str) -> Engine {
    let dir = location(tag).join(name);
    std::fs::remove_dir_all(&dir).ok();
    Engine::open(EngineConfig::durable(&dir).expect("durable location")).expect("engine opens")
}

fn scope_of(engine: &Engine) -> TenantScope {
    TenantScope::from_parts(tenant(), engine.identity())
}

/// Resolves one operation on one engine: the honest observable of a commit.
fn resolution(engine: &Engine, operation: &str) -> DurableResolution {
    engine
        .resolve_commit(
            &scope_of(engine),
            OperationId::try_new(operation).expect("valid operation"),
        )
        .expect("resolve")
}

/// The document identities one engine durably holds for the lane tenant.
fn documents_of(engine: &Engine) -> Vec<DocumentId> {
    let scope = scope_of(engine);
    let snapshot = engine.read_snapshot(&scope).expect("snapshot");
    let mut names: Vec<DocumentId> = engine
        .snapshot_documents(&scope, &snapshot)
        .expect("documents")
        .into_iter()
        .map(|document| document.id().clone())
        .collect();
    names.sort_by_key(|id| id.as_str().to_owned());
    names
}

// ---------------------------------------------------------------------------
// Shard loss assessment
// ---------------------------------------------------------------------------

#[test]
fn shard_loss_assessment_names_lost_shards_their_owners_and_domains() {
    let topology = sharded_topology();
    let assessment = ShardLoss::assess(&topology, &[member("m-a1")]).expect("assessment");

    let lost = assessment.lost_shards();
    assert_eq!(lost.len(), 1, "one shard lost its owner");
    assert_eq!(lost[0].shard(), ShardId::from_number(1));
    assert_eq!(lost[0].owner(), &member("m-a1"));
    assert_eq!(lost[0].region().map(RegionId::as_str), Some("r-alpha"));
    assert_eq!(lost[0].failure_domain(), Some("fd-r-alpha"));
    assert_eq!(lost[0].ranges().len(), 1, "the shard's bucket ranges are named");
    assert_eq!(lost[0].bucket_count(), u64::from(HALF_SPACE));

    let serving = assessment.serving_shards();
    assert_eq!(serving.len(), 1, "the healthy shard keeps serving");
    assert_eq!(serving[0].shard(), ShardId::from_number(2));
    assert_eq!(serving[0].owner(), &member("m-b1"));

    assert_eq!(assessment.lost_members(), &[member("m-a1")]);
    assert_eq!(assessment.surviving_members().len(), 5, "five members survive");
    assert!(!assessment.surviving_members().contains(&member("m-a1")));
}

#[test]
fn losing_a_member_that_owns_no_shard_loses_no_shards() {
    let topology = sharded_topology();
    let loss = ShardLoss::assess(&topology, &[member("m-g2")]).expect("assessment");
    assert!(loss.lost_shards().is_empty(), "no shard lost its owner");
    assert_eq!(loss.serving_shards().len(), 2);
    assert_eq!(loss.surviving_members().len(), 5);
}

#[test]
fn loss_assessment_refuses_unknown_members() {
    let topology = sharded_topology();
    let refused =
        ShardLoss::assess(&topology, &[member("m-unknown")]).expect_err("unknown member refused");
    assert_eq!(
        refused,
        FailoverError::UnknownLostMember { member: member("m-unknown") },
        "a member the view does not seat is refused, never guessed"
    );
}

// ---------------------------------------------------------------------------
// Failover decisions
// ---------------------------------------------------------------------------

#[test]
fn failover_transfers_lost_shards_at_raised_epochs_and_retains_healthy_ones() {
    let topology = sharded_topology();
    let loss = ShardLoss::assess(&topology, &[member("m-a1")]).expect("assessment");
    let plan = FailoverDecision::decide(
        &topology,
        &loss,
        topology.table().clone(),
        view_with_shard1_owner("m-a2", 2),
    )
    .expect("failover decision");

    let transfers = plan.transfers();
    assert_eq!(transfers.len(), 1, "the lost shard transfers");
    assert_eq!(transfers[0].shard(), ShardId::from_number(1));
    assert_eq!(transfers[0].from(), &member("m-a1"));
    assert_eq!(transfers[0].to(), &member("m-a2"));
    assert_eq!(transfers[0].from_epoch(), 1);
    assert_eq!(transfers[0].to_epoch(), 2, "the successor serves at a raised epoch");
    assert_eq!(transfers[0].buckets(), u64::from(HALF_SPACE));

    assert_eq!(plan.retained(), &[ShardId::from_number(2)], "the healthy shard is retained");
    assert!(plan.dispersed().is_empty());

    // The plan's seated topology routes the lost shard's buckets to the
    // successor at the raised epoch.
    assert_eq!(plan.topology().owner_of(ShardId::from_number(1)), Some(member("m-a2")));
    assert_eq!(plan.topology().epoch_of(ShardId::from_number(1)), Some(2));
}

#[test]
fn failover_disperses_a_lost_shards_buckets_when_no_successor_is_seated() {
    let topology = sharded_topology();
    let loss = ShardLoss::assess(&topology, &[member("m-a1")]).expect("assessment");

    // The Platform disperses shard/1's buckets into shard/2, which raises
    // shard/2's epoch because its ranges changed.
    let view = sharded_view().with_owner_at_epoch("shard/2", "m-b1", 2).expect("valid owner");
    let table = BucketRangeTable::try_new(&[BucketRange::try_new(
        0,
        BUCKET_SPACE,
        ShardId::from_number(2),
    )
    .expect("valid range")])
    .expect("valid table");

    let plan = FailoverDecision::decide(&topology, &loss, table, view).expect("dispersal");
    assert!(plan.transfers().is_empty(), "no successor shard was seated");
    let dispersed = plan.dispersed();
    assert_eq!(dispersed.len(), 1);
    assert_eq!(dispersed[0].shard(), ShardId::from_number(1));
    assert_eq!(dispersed[0].from(), &member("m-a1"));
    assert_eq!(
        dispersed[0].allocations(),
        &[(ShardId::from_number(2), u64::from(HALF_SPACE))],
        "the dispersal names where the buckets went"
    );
}

#[test]
fn failover_refuses_owner_replacement_on_a_healthy_shard() {
    let topology = sharded_topology();
    let loss = ShardLoss::assess(&topology, &[member("m-a1")]).expect("assessment");
    let view = sharded_view()
        .with_owner_at_epoch("shard/1", "m-a2", 2)
        .expect("valid owner")
        .with_owner_at_epoch("shard/2", "m-b2", 2)
        .expect("valid owner");

    let refused = FailoverDecision::decide(&topology, &loss, topology.table().clone(), view)
        .expect_err("healthy owner replacement refused");
    assert_eq!(
        refused,
        FailoverError::HealthyShardOwnerReplaced {
            shard: ShardId::from_number(2),
            owner: member("m-b1"),
        },
        "replacing a surviving shard's owner is a migration decision, not failover"
    );
}

#[test]
fn failover_refuses_shrinking_a_healthy_shard() {
    let topology = sharded_topology();
    let loss = ShardLoss::assess(&topology, &[member("m-a1")]).expect("assessment");
    // shard/2 (healthy) loses its lower half to the transferred shard/1;
    // its epoch rises because its ranges changed, so only the failover
    // rule can refuse this.
    let view = sharded_view()
        .with_owner_at_epoch("shard/1", "m-a2", 2)
        .expect("valid owner")
        .with_owner_at_epoch("shard/2", "m-b1", 2)
        .expect("valid owner");
    let three_quarters = HALF_SPACE + HALF_SPACE / 2;
    let table = BucketRangeTable::try_new(&[
        BucketRange::try_new(0, three_quarters, ShardId::from_number(1)).expect("valid range"),
        BucketRange::try_new(three_quarters, BUCKET_SPACE, ShardId::from_number(2))
            .expect("valid range"),
    ])
    .expect("valid table");

    let refused = FailoverDecision::decide(&topology, &loss, table, view)
        .expect_err("a healthy shard may not shrink under failover");
    assert_eq!(
        refused,
        FailoverError::HealthyShardShrunk { shard: ShardId::from_number(2) },
        "taking buckets from a surviving shard is a migration decision, not failover"
    );
}

#[test]
fn failover_refuses_a_lost_successor_and_a_retained_lost_owner() {
    let topology = sharded_topology();

    // The successor is itself lost.
    let loss = ShardLoss::assess(&topology, &[member("m-a1"), member("m-a2")]).expect("assessment");
    let refused = FailoverDecision::decide(
        &topology,
        &loss,
        topology.table().clone(),
        view_with_shard1_owner("m-a2", 2),
    )
    .expect_err("lost successor refused");
    assert_eq!(
        refused,
        FailoverError::SuccessorLost { shard: ShardId::from_number(1), successor: member("m-a2") }
    );

    // The offered view keeps the lost member owned at a raised epoch.
    let loss = ShardLoss::assess(&topology, &[member("m-a1")]).expect("assessment");
    let refused = FailoverDecision::decide(
        &topology,
        &loss,
        topology.table().clone(),
        view_with_shard1_owner("m-a1", 2),
    )
    .expect_err("lost owner retained refused");
    assert_eq!(
        refused,
        FailoverError::LostOwnerRetained { shard: ShardId::from_number(1), owner: member("m-a1") },
        "failover never re-seats the declared-lost owner"
    );
}

#[test]
fn failover_refuses_decisions_without_a_declared_loss() {
    let topology = sharded_topology();
    let loss = ShardLoss::assess(&topology, &[]).expect("empty loss assesses");
    let refused = FailoverDecision::decide(
        &topology,
        &loss,
        topology.table().clone(),
        view_with_shard1_owner("m-a2", 2),
    )
    .expect_err("no-loss failover refused");
    assert_eq!(refused, FailoverError::FailoverWithoutLoss);
}

#[test]
fn failover_plan_fences_the_deposed_owners_placements() {
    let topology = sharded_topology();
    let document = document_for_shard(&topology, ShardId::from_number(1));
    let placement =
        topology.route(&RoutingRequest::of_document(&tenant(), &document)).expect("routes");

    let loss = ShardLoss::assess(&topology, &[member("m-a1")]).expect("assessment");
    let plan = FailoverDecision::decide(
        &topology,
        &loss,
        topology.table().clone(),
        view_with_shard1_owner("m-a2", 2),
    )
    .expect("decision");

    let refused = plan
        .topology()
        .authorize_placement(&placement, &tenant())
        .expect_err("the deposed owner's placement is fenced");
    assert!(matches!(refused, ShardingError::PlacementOwnerSuperseded { .. }));
}

/// The next-table pair that replaces `shard/1`'s half with `shard/3` — an
/// identity the seated table does not name — leaving `shard/2` exactly as
/// seated.
fn entering_shard_table() -> BucketRangeTable {
    BucketRangeTable::try_new(&[
        BucketRange::try_new(0, HALF_SPACE, ShardId::from_number(3)).expect("valid range"),
        BucketRange::try_new(HALF_SPACE, BUCKET_SPACE, ShardId::from_number(2))
            .expect("valid range"),
    ])
    .expect("valid table")
}

/// The next-view pair seating `shard/3` under `owner` at `epoch`, with
/// `shard/2` kept exactly as the seated topology holds it.
fn entering_shard_view(owner: &str, epoch: u64) -> TopologyView {
    sharded_view()
        .with_owner_at_epoch("shard/3", owner, epoch)
        .expect("valid owner")
        .with_owner_at_epoch("shard/2", "m-b1", 1)
        .expect("valid owner")
}

#[test]
fn failover_refuses_a_lost_owner_re_seated_through_a_new_shard_identity() {
    let topology = sharded_topology();
    let loss = ShardLoss::assess(&topology, &[member("m-a1")]).expect("assessment");

    // shard/3 — an identity the table does not seat — takes shard/1's
    // buckets under the declared-lost owner at shard/1's original epoch.
    // The decision must judge every identity the next table seats, not
    // only the ones the current table already seated.
    let refused = FailoverDecision::decide(
        &topology,
        &loss,
        entering_shard_table(),
        entering_shard_view("m-a1", 1),
    )
    .expect_err("a new identity never launders a declared-lost owner back in");
    assert_eq!(
        refused,
        FailoverError::EnteringShardLostOwner {
            shard: ShardId::from_number(3),
            owner: member("m-a1"),
        },
        "the entering shard's owner is checked against the declared loss"
    );
}

#[test]
fn failover_refuses_an_entering_shard_below_the_fencing_epoch() {
    let topology = sharded_topology();
    let loss = ShardLoss::assess(&topology, &[member("m-a1")]).expect("assessment");

    // shard/3 enters under a surviving owner but at shard/1's original
    // epoch: the ownership of those buckets would end where it stood, so
    // anything fenced below that bar could return with the new identity.
    let refused = FailoverDecision::decide(
        &topology,
        &loss,
        entering_shard_table(),
        entering_shard_view("m-a2", 1),
    )
    .expect_err("an entering shard must clear the fencing bar of its buckets");
    assert_eq!(
        refused,
        FailoverError::EnteringShardEpochNotRaised {
            shard: ShardId::from_number(3),
            expectation: 1,
            offered: 1,
        },
        "the entering identity must exceed the highest epoch the table held at its buckets"
    );
}

#[test]
fn failover_accepts_a_returning_shard_identity_above_the_fencing_epoch() {
    let topology = sharded_topology();
    let loss = ShardLoss::assess(&topology, &[member("m-a1")]).expect("assessment");

    // The same seating with the epoch raised above the fencing bar is a
    // legitimate return: the lost shard dispersed into the entering
    // identity at an epoch that fences everything before it.
    let plan = FailoverDecision::decide(
        &topology,
        &loss,
        entering_shard_table(),
        entering_shard_view("m-a2", 2),
    )
    .expect("a returning identity above the fencing bar seats");
    assert!(plan.transfers().is_empty(), "no seated identity changed hands");
    assert_eq!(plan.dispersed().len(), 1);
    assert_eq!(plan.dispersed()[0].shard(), ShardId::from_number(1));
    assert_eq!(
        plan.dispersed()[0].allocations(),
        &[(ShardId::from_number(3), u64::from(HALF_SPACE))],
        "the dispersed buckets are named at their receiving identity"
    );
    assert_eq!(plan.topology().owner_of(ShardId::from_number(3)), Some(member("m-a2")));
    assert_eq!(plan.topology().epoch_of(ShardId::from_number(3)), Some(2));
}

// ---------------------------------------------------------------------------
// Cluster-scoped transactions
// ---------------------------------------------------------------------------

fn two_shard_engines(tag: &str) -> (Engine, Engine) {
    (open_engine(tag, "m-a1"), open_engine(tag, "m-b1"))
}

#[test]
fn single_shard_cluster_transaction_commits_atomically() {
    let topology = sharded_topology();
    let (engine_a, _engine_b) = two_shard_engines("atomic");
    let serving = EngineWriteServing::try_new(vec![(member("m-a1"), &engine_a)]).expect("seats");

    let first = document_for_shard(&topology, ShardId::from_number(1));
    let lane_tenant = tenant();
    let mut second = None;
    for index in 0..200u32 {
        let candidate = document(&format!("doc-{index}"));
        let request = RoutingRequest::of_document(&lane_tenant, &candidate);
        if candidate.id() != first.id()
            && topology
                .route(&request)
                .is_ok_and(|placement| placement.shard() == ShardId::from_number(1))
        {
            second = Some(candidate);
            break;
        }
    }
    let second = second.expect("a second document in the same shard");
    let second_id = second.id().clone();

    let transaction = ClusterTransaction::try_new(
        tenant(),
        OperationId::try_new("cluster-op-atomic").expect("valid operation"),
        vec![ClusterStage::Put(first.clone()), ClusterStage::Put(second)],
    )
    .expect("valid transaction");

    let routed = transaction.route(&topology).expect("routes");
    assert_eq!(routed.shard_count(), 1, "both writes route to one shard");
    assert!(routed.spans_single_shard());

    let scopes = vec![(member("m-a1"), scope_of(&engine_a))];
    let outcome = routed.execute(&topology, &serving, &scopes);
    let ClusterOutcome::Atomic(commit) = &outcome else {
        panic!("a single-shard cluster transaction is atomic, got {outcome:?}");
    };
    assert_eq!(commit.shard(), ShardId::from_number(1));
    assert_eq!(commit.receipt().durability(), LocalDurability::Synced);
    assert!(!commit.replayed(), "the first drive is a fresh commit");
    assert!(
        outcome.limits().contains(&ATOMIC_WITHIN_ONE_SHARD),
        "the recorded limit states the atomicity scope: {:?}",
        outcome.limits()
    );

    // Both documents are observable through the engine's durable state.
    let mut held = documents_of(&engine_a);
    held.sort_by_key(|id| id.as_str().to_owned());
    assert!(held.contains(first.id()), "the first document committed: {held:?}");
    assert!(held.contains(&second_id), "the second document committed: {held:?}");

    // A re-drive is idempotent: the same operation identity replays the
    // same receipt instead of committing twice.
    let replay =
        transaction.route(&topology).expect("routes").execute(&topology, &serving, &scopes);
    let ClusterOutcome::Atomic(replayed) = &replay else {
        panic!("the re-drive stays atomic, got {replay:?}");
    };
    assert!(replayed.replayed(), "the re-drive replays the committed operation");
    assert_eq!(
        replayed.receipt().sequence(),
        commit.receipt().sequence(),
        "the observable commit result is preserved"
    );
}

#[test]
fn cross_shard_cluster_transaction_is_explicitly_limited_not_atomic() {
    let topology = sharded_topology();
    let (engine_a, engine_b) = two_shard_engines("cross");
    let serving =
        EngineWriteServing::try_new(vec![(member("m-a1"), &engine_a), (member("m-b1"), &engine_b)])
            .expect("seats");

    let in_one = document_for_shard(&topology, ShardId::from_number(1));
    let in_two = document_for_shard(&topology, ShardId::from_number(2));
    let in_two_id = in_two.id().clone();
    let transaction = ClusterTransaction::try_new(
        tenant(),
        OperationId::try_new("cluster-op-cross").expect("valid operation"),
        vec![ClusterStage::Put(in_one.clone()), ClusterStage::Put(in_two)],
    )
    .expect("valid transaction");

    let routed = transaction.route(&topology).expect("routes");
    assert_eq!(routed.shard_count(), 2, "the writes span two shards");
    assert!(!routed.spans_single_shard());

    let scopes = vec![(member("m-a1"), scope_of(&engine_a)), (member("m-b1"), scope_of(&engine_b))];
    let outcome = routed.execute(&topology, &serving, &scopes);
    let ClusterOutcome::PerShard { committed } = &outcome else {
        panic!("a cross-shard commit is per-shard, got {outcome:?}");
    };
    assert_eq!(committed.len(), 2, "each shard commits its own shard-local transaction");
    assert!(
        outcome.limits().iter().any(|limit| limit.contains("not atomic")),
        "the non-atomicity limit is recorded: {:?}",
        outcome.limits()
    );
    assert!(documents_of(&engine_a).contains(in_one.id()));
    assert!(
        !documents_of(&engine_a).contains(&in_two_id),
        "each shard holds only its routed documents"
    );
}

#[test]
fn cross_shard_failure_records_the_partial_commit_without_claiming_success() {
    let topology = sharded_topology();
    let (engine_a, engine_b) = two_shard_engines("partial");
    // m-b1 has no endpoint in this serving set: its shard cannot execute.
    let serving = EngineWriteServing::try_new(vec![(member("m-a1"), &engine_a)]).expect("seats");

    let in_one = document_for_shard(&topology, ShardId::from_number(1));
    let in_two = document_for_shard(&topology, ShardId::from_number(2));
    let transaction = ClusterTransaction::try_new(
        tenant(),
        OperationId::try_new("cluster-op-partial").expect("valid operation"),
        vec![ClusterStage::Put(in_one.clone()), ClusterStage::Put(in_two)],
    )
    .expect("valid transaction");

    let scopes = vec![(member("m-a1"), scope_of(&engine_a)), (member("m-b1"), scope_of(&engine_b))];
    let outcome =
        transaction.route(&topology).expect("routes").execute(&topology, &serving, &scopes);
    let ClusterOutcome::Partial { committed, refused, .. } = &outcome else {
        panic!("a refused shard makes the outcome partial, got {outcome:?}");
    };
    assert_eq!(committed.len(), 1, "the shard that could commit did");
    assert_eq!(committed[0].shard(), ShardId::from_number(1));
    assert_eq!(refused.shard(), ShardId::from_number(2), "the refusal names the shard");
    assert!(
        matches!(refused.error(), ShardWriteError::MemberUnserved { .. }),
        "the refusal names the unserved owner: {:?}",
        refused.error()
    );
    assert!(
        outcome.limits().iter().any(|limit| limit.contains("does not roll back")),
        "the no-rollback limit is recorded: {:?}",
        outcome.limits()
    );

    // The committed shard's document is observable; the refused shard's is
    // not, and no database-wide success is claimed.
    assert!(documents_of(&engine_a).contains(in_one.id()));
}

#[test]
fn execution_refuses_a_scope_seated_for_another_tenant() {
    let topology = sharded_topology();
    let engine_a = open_engine("scope-mismatch", "m-a1");
    let serving = EngineWriteServing::try_new(vec![(member("m-a1"), &engine_a)]).expect("seats");

    let document = document_for_shard(&topology, ShardId::from_number(1));
    let transaction = ClusterTransaction::try_new(
        tenant(),
        OperationId::try_new("cluster-op-scope-tenant").expect("valid operation"),
        vec![ClusterStage::Put(document)],
    )
    .expect("valid transaction");

    // The caller seats m-a1's scope for a foreign tenant: execution must
    // refuse rather than write the transaction through another tenant's
    // context.
    let foreign = TenantId::try_new("tenant-foreign").expect("valid tenant");
    let scopes =
        vec![(member("m-a1"), TenantScope::from_parts(foreign.clone(), engine_a.identity()))];
    let outcome =
        transaction.route(&topology).expect("routes").execute(&topology, &serving, &scopes);
    let ClusterOutcome::Refused { refused } = &outcome else {
        panic!("a seated scope of another tenant refuses, got {outcome:?}");
    };
    assert_eq!(refused.shard(), ShardId::from_number(1));
    assert_eq!(
        refused.error(),
        &ShardWriteError::ScopeTenantMismatch {
            owner: member("m-a1"),
            routed_for: tenant(),
            seated: foreign,
        },
        "the refusal names the seated tenant against the routed one"
    );
    assert!(documents_of(&engine_a).is_empty(), "the refused write landed under no tenant");
}

/// A three-shard topology: one third of the bucket space per shard, owned
/// by one Member per shard at epoch 1.
fn three_shard_topology() -> ShardTopology {
    let third = BUCKET_SPACE / 3;
    let two_thirds = 2 * third;
    let view = sharded_view()
        .with_owner("shard/1", "m-a1")
        .expect("valid owner")
        .with_owner("shard/2", "m-b1")
        .expect("valid owner")
        .with_owner("shard/3", "m-g1")
        .expect("valid owner");
    let table = BucketRangeTable::try_new(&[
        BucketRange::try_new(0, third, ShardId::from_number(1)).expect("valid range"),
        BucketRange::try_new(third, two_thirds, ShardId::from_number(2)).expect("valid range"),
        BucketRange::try_new(two_thirds, BUCKET_SPACE, ShardId::from_number(3))
            .expect("valid range"),
    ])
    .expect("valid table");
    ShardTopology::try_new(table, view).expect("seats")
}

#[test]
fn partial_outcomes_name_the_shards_execution_never_reached() {
    let topology = three_shard_topology();
    let engine_a = open_engine("partial-three", "m-a1");
    let engine_b = open_engine("partial-three", "m-b1");
    let engine_g = open_engine("partial-three", "m-g1");
    // m-b1's endpoint is absent: shard/2 refuses after shard/1 committed,
    // and shard/3 is never attempted.
    let serving =
        EngineWriteServing::try_new(vec![(member("m-a1"), &engine_a), (member("m-g1"), &engine_g)])
            .expect("seats");

    let stages = [1, 2, 3]
        .map(|number| {
            ClusterStage::Put(document_for_shard(&topology, ShardId::from_number(number)))
        })
        .to_vec();
    let transaction = ClusterTransaction::try_new(
        tenant(),
        OperationId::try_new("cluster-op-three").expect("valid operation"),
        stages,
    )
    .expect("valid transaction");
    let routed = transaction.route(&topology).expect("routes");
    assert_eq!(routed.shard_count(), 3, "the transaction spans three shards");

    let scopes = vec![
        (member("m-a1"), scope_of(&engine_a)),
        (member("m-b1"), scope_of(&engine_b)),
        (member("m-g1"), scope_of(&engine_g)),
    ];
    let outcome = routed.execute(&topology, &serving, &scopes);
    let ClusterOutcome::Partial { committed, refused, unattempted } = &outcome else {
        panic!("a refusal after a commit is partial, got {outcome:?}");
    };
    assert_eq!(committed.len(), 1, "shard/1 committed before the refusal");
    assert_eq!(committed[0].shard(), ShardId::from_number(1));
    assert_eq!(refused.shard(), ShardId::from_number(2));
    assert_eq!(
        unattempted,
        &[ShardId::from_number(3)],
        "the never-attempted shard is named, not silently dropped"
    );
    // The three outcome parts partition the routed shards exactly.
    let mut partitioned: Vec<ShardId> = committed.iter().map(ShardCommit::shard).collect();
    partitioned.push(refused.shard());
    partitioned.extend_from_slice(unattempted);
    partitioned.sort_unstable();
    let mut routed_shards: Vec<ShardId> =
        routed.groups().iter().map(ShardWriteSet::shard).collect();
    routed_shards.sort_unstable();
    assert_eq!(partitioned, routed_shards, "every routed shard appears in exactly one part");
    assert!(documents_of(&engine_g).is_empty(), "the unattempted shard executed nothing");

    // Refused at the very first shard of three: nothing commits, the
    // outcome is the refusal, and no later shard executed anything.
    let first_refused_serving =
        EngineWriteServing::try_new(vec![(member("m-g1"), &engine_g)]).expect("seats");
    let outcome = transaction.route(&topology).expect("routes").execute(
        &topology,
        &first_refused_serving,
        &scopes,
    );
    let ClusterOutcome::Refused { refused } = &outcome else {
        panic!("a refusal before any commit names only the refusal, got {outcome:?}");
    };
    assert_eq!(refused.shard(), ShardId::from_number(1));
    assert!(
        documents_of(&engine_b).is_empty() && documents_of(&engine_g).is_empty(),
        "execution never reached past the first refusal"
    );
}

#[test]
fn a_failover_between_routing_and_execution_fences_the_stale_shard() {
    let topology = sharded_topology();
    let engine_a2 = open_engine("fenced", "m-a2");
    // The routing topology seats m-a1; the execution topology has already
    // transferred shard/1 to m-a2 at epoch 2.
    let serving = EngineWriteServing::try_new(vec![(member("m-a2"), &engine_a2)]).expect("seats");
    let loss = ShardLoss::assess(&topology, &[member("m-a1")]).expect("assessment");
    let plan = FailoverDecision::decide(
        &topology,
        &loss,
        topology.table().clone(),
        view_with_shard1_owner("m-a2", 2),
    )
    .expect("decision");

    let document = document_for_shard(&topology, ShardId::from_number(1));
    let transaction = ClusterTransaction::try_new(
        tenant(),
        OperationId::try_new("cluster-op-fenced").expect("valid operation"),
        vec![ClusterStage::Put(document)],
    )
    .expect("valid transaction");

    let scopes = vec![(member("m-a2"), scope_of(&engine_a2))];
    let routed = transaction.route(&topology).expect("routes under the stale view");
    let outcome = routed.execute(plan.topology(), &serving, &scopes);
    let ClusterOutcome::Refused { refused } = &outcome else {
        panic!("the fenced placement is refused, got {outcome:?}");
    };
    assert_eq!(refused.shard(), ShardId::from_number(1));
    assert!(
        matches!(
            refused.error(),
            ShardWriteError::PlacementFenced(ShardingError::PlacementOwnerSuperseded { .. })
        ),
        "the refusal carries the fencing fact: {:?}",
        refused.error()
    );
    assert!(documents_of(&engine_a2).is_empty(), "the fenced write landed nowhere");
}

#[test]
fn an_empty_cluster_transaction_is_refused() {
    let refused = ClusterTransaction::try_new(
        tenant(),
        OperationId::try_new("cluster-op-empty").expect("valid operation"),
        Vec::new(),
    )
    .expect_err("an empty transaction is refused");
    assert_eq!(refused, ClusterTransactionError::Empty);
}

#[test]
fn shard_refusal_shapes_are_named_and_secret_safe() {
    let refusal = ShardRefusal::new(
        ShardId::from_number(1),
        ShardWriteError::MemberUnserved { owner: member("m-a1") },
    );
    assert_eq!(refusal.shard(), ShardId::from_number(1));
    let rendered = format!("{refusal:?}");
    assert!(!rendered.contains("/home/"), "no path crosses the refusal shape");
}

// ---------------------------------------------------------------------------
// Per-shard recovery reporting
// ---------------------------------------------------------------------------

fn sequence(value: u64) -> crate::types::StorageSequence {
    crate::types::StorageSequence::new(value)
}

#[test]
fn recovery_report_names_every_shard_member_and_owner_verdict() {
    let topology = sharded_topology();
    let report = ShardRecoveryReport::try_new(
        &topology,
        &[
            (
                ShardId::from_number(1),
                vec![
                    MemberRecovery::holds(member("m-a1"), Some(sequence(3))),
                    MemberRecovery::absent(member("m-a2")),
                ],
            ),
            (
                ShardId::from_number(2),
                vec![
                    MemberRecovery::holds(member("m-b1"), Some(sequence(7))),
                    MemberRecovery::holds(member("m-b2"), Some(sequence(7))),
                ],
            ),
        ],
    )
    .expect("assembles");

    assert!(report.recovered(ShardId::from_number(1)), "the owner holds the shard's data");
    assert!(report.recovered(ShardId::from_number(2)));
    assert!(report.unrecovered_shards().is_empty());
    assert_eq!(
        report.members_holding(ShardId::from_number(2)),
        vec![member("m-b1"), member("m-b2")],
        "every holding member is named"
    );

    let rendered = report.summary();
    assert!(rendered.contains("shard/1") && rendered.contains("shard/2"), "{rendered}");
    assert!(rendered.contains("m-a1") && rendered.contains("m-b2"), "{rendered}");
}

#[test]
fn recovery_report_marks_shards_unrecovered_when_the_owner_is_absent() {
    let topology = sharded_topology();
    let report = ShardRecoveryReport::try_new(
        &topology,
        &[
            (
                ShardId::from_number(1),
                vec![
                    MemberRecovery::absent(member("m-a1")),
                    MemberRecovery::holds(member("m-a2"), Some(sequence(3))),
                ],
            ),
            (ShardId::from_number(2), vec![MemberRecovery::holds(member("m-b1"), None)]),
        ],
    )
    .expect("assembles");

    assert!(!report.recovered(ShardId::from_number(1)), "the owner holds nothing");
    assert_eq!(report.unrecovered_shards(), vec![ShardId::from_number(1)]);
    assert_eq!(report.members_holding(ShardId::from_number(1)), vec![member("m-a2")]);
}

#[test]
fn recovery_report_refuses_unknown_members_and_incomplete_shard_sets() {
    let topology = sharded_topology();

    let refused = ShardRecoveryReport::try_new(
        &topology,
        &[(ShardId::from_number(1), vec![MemberRecovery::absent(member("m-ghost"))])],
    )
    .expect_err("unknown member refused");
    assert_eq!(refused, RecoveryError::MemberNotSeated { member: member("m-ghost") });

    let refused = ShardRecoveryReport::try_new(
        &topology,
        &[(ShardId::from_number(9), vec![MemberRecovery::absent(member("m-a1"))])],
    )
    .expect_err("unseated shard refused");
    assert_eq!(refused, RecoveryError::ShardNotSeated { shard: ShardId::from_number(9) });

    let refused = ShardRecoveryReport::try_new(
        &topology,
        &[(ShardId::from_number(1), vec![MemberRecovery::absent(member("m-a1"))])],
    )
    .expect_err("an incomplete shard set is refused");
    assert_eq!(refused, RecoveryError::ShardMissing { shard: ShardId::from_number(2) });
}

// ---------------------------------------------------------------------------
// Acknowledgement truth
// ---------------------------------------------------------------------------

#[test]
fn a_shard_local_commit_is_reported_as_shard_scoped_never_database_success() {
    let topology = sharded_topology();
    let (engine_a, engine_b) = two_shard_engines("shard-scoped");
    let serving =
        EngineWriteServing::try_new(vec![(member("m-a1"), &engine_a), (member("m-b1"), &engine_b)])
            .expect("seats");

    let in_two = document_for_shard(&topology, ShardId::from_number(2));
    let transaction = ClusterTransaction::try_new(
        tenant(),
        OperationId::try_new("cluster-op-scoped").expect("valid operation"),
        vec![ClusterStage::Put(in_two)],
    )
    .expect("valid transaction");

    let scopes = vec![(member("m-a1"), scope_of(&engine_a)), (member("m-b1"), scope_of(&engine_b))];
    let outcome =
        transaction.route(&topology).expect("routes").execute(&topology, &serving, &scopes);
    let ClusterOutcome::Atomic(commit) = &outcome else {
        panic!("expected an atomic single-shard commit, got {outcome:?}");
    };
    // The receipt is a shard-local durability fact; the other shard's
    // engine holds nothing of it.
    assert!(matches!(resolution(&engine_b, "cluster-op-scoped"), DurableResolution::Committed(_)));
    assert!(
        documents_of(&engine_a).is_empty(),
        "the shard-local commit never crosses into the other shard"
    );
    assert_eq!(commit.shard(), ShardId::from_number(2));
}
