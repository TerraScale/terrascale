//! External-caller construction of the SH-05 coordination surface
//! (failover decisions, cluster transactions, per-shard recovery reports).
//!
//! These tests use only what `terrabase_v2::sharding::coordination` and
//! its public dependencies expose: the sharding topology seam and the
//! coordination types. The data-bearing execution and fault scenarios
//! live in the crate-private suite and lane: execution needs a
//! Platform-minted `TenantScope`, which the engine seam keeps without a
//! public constructor by design (PD-2026-08-26-11), and the lane's
//! delivery replay uses crate-internal engine primitives. This file
//! proves what an external caller can construct, decide, route, and
//! observe today, mirroring the SH-04 public-surface finding.
//!
//! No claim here extends beyond observable behavior; the tracked lane
//! receipts under `docs/evidence/sharding/` record the qualification runs.

use terrabase_v2::Engine;
use terrabase_v2::EngineConfig;
use terrabase_v2::replication::{MemberId, TopologyView};
use terrabase_v2::sharding::coordination::{
    ClusterStage, ClusterTransaction, EngineWriteServing, FailoverDecision, FailoverError,
    MemberRecovery, ShardLoss, ShardRecoveryReport,
};
use terrabase_v2::sharding::{
    BUCKET_SPACE, BucketRange, BucketRangeTable, RoutingRequest, ShardId, ShardTopology,
};
use terrabase_v2::types::{
    CollectionName, Document, DocumentId, DocumentValue, OperationId, TenantId,
};

/// The lane membership with no shard ownership yet.
fn membership() -> TopologyView {
    let mut view = TopologyView::new();
    for region in ["r-alpha", "r-beta", "r-gamma"] {
        view = view.with_region(region).expect("valid region");
    }
    for (member, region) in [
        ("m-a1", "r-alpha"),
        ("m-a2", "r-alpha"),
        ("m-b1", "r-beta"),
        ("m-b2", "r-beta"),
        ("m-g1", "r-gamma"),
        ("m-g2", "r-gamma"),
    ] {
        view = view.with_member(member, region, &format!("fd-{region}")).expect("valid member");
    }
    view
}

/// The seated topology's view: shard/1 under m-a1, shard/2 under m-b1.
fn view() -> TopologyView {
    membership()
        .with_owner("shard/1", "m-a1")
        .expect("valid owner")
        .with_owner("shard/2", "m-b1")
        .expect("valid owner")
}

fn topology() -> ShardTopology {
    let half = BUCKET_SPACE / 2;
    let table = BucketRangeTable::try_new(&[
        BucketRange::try_new(0, half, ShardId::from_number(1)).expect("valid range"),
        BucketRange::try_new(half, BUCKET_SPACE, ShardId::from_number(2)).expect("valid range"),
    ])
    .expect("valid table");
    ShardTopology::try_new(table, view()).expect("seats")
}

fn tenant() -> TenantId {
    TenantId::try_new("tenant-public").expect("valid tenant")
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
    for index in 0..400u32 {
        let candidate = document(&format!("pub-doc-{index}"));
        let request = RoutingRequest::of_document(&tenant, &candidate);
        if let Ok(placement) = topology.route(&request)
            && placement.shard() == shard
        {
            return candidate;
        }
    }
    panic!("no document found for shard {shard:?}");
}

fn open_engine(tag: &str, name: &str) -> Engine {
    let dir =
        std::env::temp_dir().join(format!("tbv2-sh05-pub-{tag}-{name}-{}", std::process::id()));
    std::fs::remove_dir_all(&dir).ok();
    Engine::open(EngineConfig::durable(&dir).expect("durable location")).expect("engine opens")
}

#[test]
fn external_caller_assesses_loss_and_decides_a_failover() {
    let topology = topology();
    let loss = ShardLoss::assess(&topology, &[MemberId::try_new("m-a1").expect("member")])
        .expect("assessment");
    assert_eq!(loss.lost_shards().len(), 1);
    assert_eq!(loss.serving_shards().len(), 1);

    let next = membership()
        .with_owner_at_epoch("shard/1", "m-a2", 2)
        .expect("valid owner")
        .with_owner_at_epoch("shard/2", "m-b1", 1)
        .expect("valid owner");
    let plan = FailoverDecision::decide(&topology, &loss, topology.table().clone(), next)
        .expect("failover decision");
    assert_eq!(plan.transfers()[0].to(), &MemberId::try_new("m-a2").expect("member"));

    // The receipt renders secret-safe: identities and epochs only.
    let rendered = plan.receipt().summary();
    assert!(rendered.contains("shard transferred: shard/1 from m-a1 epoch 1 to m-a2 epoch 2"));
    assert!(
        !rendered.contains(std::path::MAIN_SEPARATOR.to_string().as_str())
            || !rendered.contains("home"),
        "no private path crosses the receipt"
    );
}

#[test]
fn external_failover_decision_refuses_a_healthy_owner_replacement() {
    let topology = topology();
    let loss = ShardLoss::assess(&topology, &[MemberId::try_new("m-a1").expect("member")])
        .expect("assessment");
    let next = membership()
        .with_owner_at_epoch("shard/1", "m-a2", 2)
        .expect("valid owner")
        .with_owner_at_epoch("shard/2", "m-b2", 2)
        .expect("valid owner");
    let refused = FailoverDecision::decide(&topology, &loss, topology.table().clone(), next)
        .expect_err("refused");
    assert!(matches!(
        refused,
        FailoverError::HealthyShardOwnerReplaced { shard, .. } if shard == ShardId::from_number(2)
    ));
}

#[test]
fn external_caller_routes_cluster_transactions_by_shard() {
    let topology = topology();
    let in_one = document_for_shard(&topology, ShardId::from_number(1));
    let in_two = document_for_shard(&topology, ShardId::from_number(2));
    let transaction = ClusterTransaction::try_new(
        tenant(),
        OperationId::try_new("pub-cluster-op-1").expect("valid operation"),
        vec![ClusterStage::Put(in_one), ClusterStage::Put(in_two)],
    )
    .expect("valid transaction");

    let routed = transaction.route(&topology).expect("routes");
    assert_eq!(routed.shard_count(), 2);
    assert!(!routed.spans_single_shard());
    assert_eq!(routed.groups()[0].shard(), ShardId::from_number(1));
    assert_eq!(routed.groups()[0].owner(), &MemberId::try_new("m-a1").expect("member"));
    assert_eq!(routed.groups()[1].epoch(), 1);
}

#[test]
fn external_serving_seats_engines_and_refuses_duplicates() {
    let engine = open_engine("serving", "m-a1");
    let member = MemberId::try_new("m-a1").expect("member");
    let duplicate = EngineWriteServing::try_new(vec![(member.clone(), &engine), (member, &engine)])
        .err()
        .expect("a Member seated twice is refused");
    assert_eq!(duplicate, MemberId::try_new("m-a1").expect("member"));

    let serving =
        EngineWriteServing::try_new(vec![(MemberId::try_new("m-a1").expect("member"), &engine)])
            .expect("seats");
    let _ = &serving;
}

#[test]
fn cluster_atomicity_limits_are_public_constants() {
    let limits = [
        terrabase_v2::sharding::coordination::ATOMIC_WITHIN_ONE_SHARD,
        terrabase_v2::sharding::coordination::CROSS_SHARD_NOT_ATOMIC,
        terrabase_v2::sharding::coordination::NO_CROSS_SHARD_ROLLBACK,
        terrabase_v2::sharding::coordination::SHARD_LOCAL_ACKNOWLEDGEMENT,
    ];
    assert!(limits.iter().all(|limit| !limit.is_empty()));
    assert!(limits[1].contains("not atomic"), "{}", limits[1]);
    assert!(limits[2].contains("does not roll back"), "{}", limits[2]);
}

#[test]
fn external_caller_assembles_a_shard_recovery_report() {
    let topology = topology();
    let report = ShardRecoveryReport::try_new(
        &topology,
        &[
            (
                ShardId::from_number(1),
                vec![MemberRecovery::holds(MemberId::try_new("m-a1").expect("member"), None)],
            ),
            (
                ShardId::from_number(2),
                vec![MemberRecovery::absent(MemberId::try_new("m-b1").expect("member"))],
            ),
        ],
    )
    .expect("assembles");
    assert!(report.recovered(ShardId::from_number(1)));
    assert_eq!(report.unrecovered_shards(), vec![ShardId::from_number(2)]);
    let rendered = report.summary();
    assert!(rendered.contains("shard/2") && rendered.contains("recovered: no"));
}
