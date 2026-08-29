//! The crate-private sharded fault lane (SH-05 qualification scenarios,
//! PB-5040/5041/5048).
//!
//! The lane builds the charter's multi-Region topology over real durable
//! Engines — three Regions, two Members per Region, one failure domain per
//! Region, two shards owned across two Regions — and drives the
//! deterministic scenarios that extend the multi-region qualification
//! matrix with sharded topologies:
//!
//! - shard-owner loss with truthful acknowledgement and a validated
//!   failover (the `old_owner_is_fenced` class the dispositions named);
//! - whole-Region loss after acknowledged sharded writes, with per-shard
//!   RPO measured as acknowledged-but-absent operations;
//! - a cross-shard cluster transaction that refuses partially, then
//!   completes idempotently after failover;
//! - a Member lost beyond its backup boundary, restored and rejoined,
//!   with the recovery window reported per shard.
//!
//! Determinism: no clock, thread, async runtime, or wall time anywhere —
//! every scenario is a fixed script over in-process Engines, and the
//! receipt test asserts that two runs render byte-equal receipts. Nothing
//! here is production machinery and no number leaves the lane.

use std::fmt::Write as _;
use std::path::PathBuf;

use crate::backup::{BackupPlan, RestorePlan, run_backup};
use crate::engine::{CommitOutcome, DurableResolution, Engine, EngineConfig};
use crate::replication::{MemberId, TopologyView};
use crate::sharding::coordination::{
    ClusterOutcome, ClusterStage, ClusterTransaction, EngineWriteServing, FailoverDecision,
    MemberRecovery, ShardLoss, ShardRecoveryReport,
};
use crate::sharding::{
    BUCKET_SPACE, BucketRange, BucketRangeTable, RoutingRequest, ShardId, ShardTopology,
};
use crate::storage::PublishedCommit;
use crate::types::{
    CollectionName, Document, DocumentId, DocumentValue, ExpectedVersion, LocalDurability,
    OperationId, TenantId, TenantScope,
};

/// The lane's Regions, in canonical order.
pub(crate) const LANE_REGIONS: [&str; 3] = ["r-alpha", "r-beta", "r-gamma"];

/// The lane's Members and their Region seats, in canonical order.
pub(crate) const LANE_MEMBERS: [(&str, &str); 6] = [
    ("m-a1", "r-alpha"),
    ("m-a2", "r-alpha"),
    ("m-b1", "r-beta"),
    ("m-b2", "r-beta"),
    ("m-g1", "r-gamma"),
    ("m-g2", "r-gamma"),
];

/// The lane's tenant.
pub(crate) const LANE_TENANT: &str = "tenant-lane";

/// The lane's document collection.
const LANE_COLLECTION: &str = "orders";

/// One lane temp directory root, unique per test binary run.
pub(crate) fn location(tag: &str) -> PathBuf {
    std::env::temp_dir().join(format!("tbv2-sh05-lane-{tag}-{}", std::process::id()))
}

/// The lane's membership view with no shard ownership yet.
fn lane_view() -> TopologyView {
    let mut view = TopologyView::new();
    for region in LANE_REGIONS {
        view = view.with_region(region).expect("valid region");
    }
    for (member, region) in LANE_MEMBERS {
        view = view.with_member(member, region, &format!("fd-{region}")).expect("valid member");
    }
    view.validated().expect("servable lane view")
}

/// The lane's base two-shard topology: `shard/1` owned by `m-a1`
/// (r-alpha), `shard/2` owned by `m-b1` (r-beta), the bucket space split
/// in half.
pub(crate) fn lane_topology() -> ShardTopology {
    let view = lane_view()
        .with_owner("shard/1", "m-a1")
        .expect("valid owner")
        .with_owner("shard/2", "m-b1")
        .expect("valid owner");
    let half = BUCKET_SPACE / 2;
    let table = BucketRangeTable::try_new(&[
        BucketRange::try_new(0, half, ShardId::from_number(1)).expect("valid range"),
        BucketRange::try_new(half, BUCKET_SPACE, ShardId::from_number(2)).expect("valid range"),
    ])
    .expect("valid lane table");
    ShardTopology::try_new(table, view).expect("lane topology seats")
}

/// The lane's split table, shared by every scenario view.
fn lane_table() -> BucketRangeTable {
    lane_topology().table().clone()
}

/// One lane document.
pub(crate) fn lane_document(name: &str) -> Document {
    Document::new(
        DocumentId::try_new(name).expect("valid identity"),
        CollectionName::try_new(LANE_COLLECTION).expect("valid collection"),
        DocumentValue::String(format!("payload-{name}")),
    )
}

/// Finds one document that routes to the wanted shard under identity keys.
pub(crate) fn document_for_shard(
    topology: &ShardTopology,
    shard: ShardId,
    seed: usize,
) -> Document {
    let tenant = TenantId::try_new(LANE_TENANT).expect("valid tenant");
    for index in 0..400u32 {
        let name = format!("s{seed}-doc-{index}");
        let candidate = lane_document(&name);
        let request = RoutingRequest::of_document(&tenant, &candidate);
        if let Ok(placement) = topology.route(&request)
            && placement.shard() == shard
        {
            return candidate;
        }
    }
    panic!("no document found for shard {shard:?}");
}

/// The lane harness: six durable Engines, one per Member, over the base
/// two-shard topology.
pub(crate) struct ShardedLane {
    /// The lane's base topology.
    pub(crate) topology: ShardTopology,
    /// Each Member's Engine; `None` while the Member is lost.
    pub(crate) engines: Vec<(MemberId, Option<Engine>)>,
    /// The lane tenant's identity.
    pub(crate) tenant: TenantId,
    dirs: Vec<PathBuf>,
}

impl ShardedLane {
    /// Builds the lane over fresh durable locations.
    pub(crate) fn new(tag: &str) -> Self {
        let topology = lane_topology();
        let tenant = TenantId::try_new(LANE_TENANT).expect("valid tenant");
        let mut engines = Vec::new();
        let mut dirs = Vec::new();
        for (name, _) in LANE_MEMBERS {
            let dir = location(tag).join(name);
            std::fs::remove_dir_all(&dir).ok();
            let engine = Engine::open(
                EngineConfig::durable(&dir)
                    .expect("durable location")
                    .with_storage_backend(crate::engine::StorageBackend::RootFiles),
            )
            .expect("engine");
            engines.push((MemberId::try_new(name).expect("valid member"), Some(engine)));
            dirs.push(dir);
        }
        Self { topology, engines, tenant, dirs }
    }

    /// Returns the Member identity at the lane roster position.
    pub(crate) fn member_id(name: &str) -> MemberId {
        MemberId::try_new(name).expect("valid lane member")
    }

    /// Returns the durable location one Member runs from.
    pub(crate) fn dir_of(&self, name: &str) -> PathBuf {
        let index = Self::index_of(name);
        self.dirs[index].clone()
    }

    fn index_of(name: &str) -> usize {
        LANE_MEMBERS.iter().position(|(member, _)| *member == name).expect("lane member")
    }

    /// Returns one Member's open Engine.
    pub(crate) fn engine(&self, name: &str) -> &Engine {
        self.engines[Self::index_of(name)].1.as_ref().expect("engine open")
    }

    /// Closes one Member's Engine (the Member is lost; its bytes remain).
    pub(crate) fn close(&mut self, name: &str) {
        let index = Self::index_of(name);
        if let Some(engine) = self.engines[index].1.take() {
            engine.shutdown().expect("engine shuts down");
        }
    }

    /// Replaces one Member's Engine (a rejoined or restored location).
    pub(crate) fn replace_engine(&mut self, name: &str, engine: Engine) {
        let index = Self::index_of(name);
        self.engines[index].1 = Some(engine);
    }

    /// Returns the open Member names.
    pub(crate) fn open_members(&self) -> Vec<&'static str> {
        LANE_MEMBERS
            .iter()
            .filter(|(member, _)| self.engines[Self::index_of(member)].1.is_some())
            .map(|(member, _)| *member)
            .collect()
    }

    /// Builds the tenant scope for one Member's Engine.
    pub(crate) fn scope_of(&self, name: &str) -> TenantScope {
        TenantScope::from_parts(self.tenant.clone(), self.engine(name).identity())
    }

    /// Builds a serving set over the named open Members.
    pub(crate) fn serving(&self, names: &[&str]) -> EngineWriteServing<'_> {
        let endpoints =
            names.iter().map(|name| (Self::member_id(name), self.engine(name))).collect();
        EngineWriteServing::try_new(endpoints).expect("distinct members")
    }

    /// Commits one cluster transaction over the named topology through the
    /// named Members, seating the lane tenant's scope for each.
    pub(crate) fn write(
        &self,
        topology: &ShardTopology,
        operation: &str,
        documents: Vec<Document>,
        serving_names: &[&str],
    ) -> ClusterOutcome {
        let transaction = ClusterTransaction::try_new(
            self.tenant.clone(),
            OperationId::try_new(operation).expect("valid operation"),
            documents.into_iter().map(ClusterStage::Put).collect(),
        )
        .expect("valid transaction");
        let scopes = serving_names
            .iter()
            .map(|name| (Self::member_id(name), self.scope_of(name)))
            .collect::<Vec<_>>();
        transaction.route(topology).expect("routes").execute(
            topology,
            &self.serving(serving_names),
            &scopes,
        )
    }

    /// Reports whether one Member's engine durably holds one operation.
    pub(crate) fn holds_operation(&self, name: &str, operation: &str) -> bool {
        if self.engines[Self::index_of(name)].1.is_none() {
            return false;
        }
        matches!(
            self.engine(name).resolve_commit(
                &self.scope_of(name),
                OperationId::try_new(operation).expect("valid operation")
            ),
            Ok(DurableResolution::Committed(_))
        )
    }

    /// The document identities one open Member's engine durably holds.
    pub(crate) fn documents_of(&self, name: &str) -> Vec<DocumentId> {
        let scope = self.scope_of(name);
        let engine = self.engine(name);
        let snapshot = engine.read_snapshot(&scope).expect("snapshot");
        engine
            .snapshot_documents(&scope, &snapshot)
            .expect("documents")
            .into_iter()
            .map(|document| document.id().clone())
            .collect()
    }

    /// Replays one tenant's published commits from one Member's engine to
    /// another's, exactly as the replication plane's replay rules do:
    /// resolve-first (already-applied operations are duplicates), stage
    /// with `ExpectedVersion::Any`, commit at `Synced`. Bounded, in-process
    /// delivery for lane scenarios only.
    pub(crate) fn deliver(&self, from: &str, to: &str) -> usize {
        let source = self.engine(from);
        let destination = self.engine(to);
        let destination_scope = self.scope_of(to);
        let mut applied = 0;
        let commits = source
            .tenant_commits(&self.tenant, crate::types::StorageSequence::new(0), u32::MAX / 2)
            .expect("tenant commits");
        for commit in &commits {
            if replay_commit(destination, &destination_scope, commit) {
                applied += 1;
            }
        }
        applied
    }

    /// Assembles the recovery observations for a report: for each seated
    /// shard, every Member's holding verdict against the shard's probe
    /// documents, with the Member's durable high water as the `through`
    /// bound.
    pub(crate) fn observe_recovery(
        &self,
        topology: &ShardTopology,
        probes: &[(ShardId, Vec<DocumentId>)],
    ) -> Vec<(ShardId, Vec<MemberRecovery>)> {
        topology
            .table()
            .shards()
            .into_iter()
            .map(|shard| {
                let probe_documents = probes
                    .iter()
                    .find(|(probe_shard, _)| *probe_shard == shard)
                    .map_or(&[] as &[DocumentId], |(_, documents)| documents.as_slice());
                let holdings = LANE_MEMBERS
                    .iter()
                    .map(|(member, _)| {
                        let open = self.engines[Self::index_of(member)].1.is_some();
                        if !open {
                            return MemberRecovery::absent(Self::member_id(member));
                        }
                        let held = self.documents_of(member);
                        let holds = probe_documents.iter().any(|document| held.contains(document));
                        let through = self.engine(member).journaled_high_water().expect("water");
                        MemberRecovery::observed(Self::member_id(member), holds, through)
                    })
                    .collect();
                (shard, holdings)
            })
            .collect()
    }
}

/// Replays one published commit into a destination engine. Returns whether
/// it applied (a duplicate resolves and applies nothing).
fn replay_commit(destination: &Engine, scope: &TenantScope, commit: &PublishedCommit) -> bool {
    if let Ok(DurableResolution::Committed(_)) =
        destination.resolve_commit(scope, commit.operation_id().clone())
    {
        return false;
    }
    let snapshot = destination.read_snapshot(scope).expect("snapshot");
    let mut transaction = destination
        .begin(scope, snapshot, commit.operation_id().clone())
        .expect("transaction begins");
    for write in commit.writes() {
        match write {
            crate::storage::PublishedWrite::Put { document } => {
                transaction.put(document.clone(), ExpectedVersion::Any).expect("put replays");
            }
            crate::storage::PublishedWrite::Delete { id, .. } => {
                transaction.delete(id.clone(), ExpectedVersion::Any).expect("delete replays");
            }
        }
    }
    matches!(transaction.commit(LocalDurability::Synced), CommitOutcome::Committed(_))
}

/// Counts the lane's acknowledged-but-absent operations across every open
/// Member: the per-shard RPO fact, measured only from engine truth.
fn rpo_of(lane: &ShardedLane, acknowledged: &[&str]) -> usize {
    acknowledged
        .iter()
        .filter(|operation| {
            !lane.open_members().iter().any(|member| lane.holds_operation(member, operation))
        })
        .count()
}

/// Prefixes one rendered lane receipt with its fault line, drawn from the
/// accepted W4 fault vocabulary (`docs/evidence/replication/`
/// `w4-lane-three-region-composite.md`): `leader-loss`, `healing`,
/// `region-loss-after-acknowledged-write`, `member-restart`,
/// `member-rejoin`. Every SH-05 receipt names exactly the faults its
/// scenario injects — no invented fault names.
fn with_faults(faults: &str, receipt: &str) -> String {
    format!("faults: {faults}\n{receipt}")
}

// ---------------------------------------------------------------------------
// Scenario 1: shard-owner loss, truthful acknowledgement, validated
// failover, deposed-owner fencing
// ---------------------------------------------------------------------------

/// Runs the shard-owner-loss scenario once and renders its receipt.
fn shard_owner_loss_receipt(tag: &str) -> String {
    let mut lane = ShardedLane::new(tag);
    let topology = lane_topology();

    // Healthy start: one write per shard, delivered to every Member (the
    // acknowledgement lattice's every-Member fact, driven by lane replay).
    let in_one = document_for_shard(&topology, ShardId::from_number(1), 1);
    let in_two = document_for_shard(&topology, ShardId::from_number(2), 1);
    let op_one = "lane-op-1";
    let op_two = "lane-op-2";
    let outcome = lane.write(&topology, op_one, vec![in_one.clone()], &["m-a1"]);
    assert!(matches!(outcome, ClusterOutcome::Atomic(_)), "{outcome:?}");
    let outcome = lane.write(&topology, op_two, vec![in_two.clone()], &["m-b1"]);
    assert!(matches!(outcome, ClusterOutcome::Atomic(_)), "{outcome:?}");
    for to in ["m-a2", "m-b1", "m-b2", "m-g1", "m-g2"] {
        lane.deliver("m-a1", to);
    }
    for to in ["m-a1", "m-a2", "m-b2", "m-g1", "m-g2"] {
        lane.deliver("m-b1", to);
    }
    for member in ["m-a1", "m-a2", "m-b1", "m-b2", "m-g1", "m-g2"] {
        assert!(lane.holds_operation(member, op_one), "{member} holds {op_one}");
        assert!(lane.holds_operation(member, op_two), "{member} holds {op_two}");
    }

    // The owner of shard/1 is lost.
    lane.close("m-a1");
    let loss =
        ShardLoss::assess(&lane.topology, &[ShardedLane::member_id("m-a1")]).expect("assessment");
    assert_eq!(loss.lost_shards().len(), 1);
    assert_eq!(loss.serving_shards().len(), 1);

    // Truthful acknowledgement under the loss: a write to the lost shard
    // refuses before commit (its owner has no endpoint) and lands nowhere;
    // the surviving shard still commits its own shard-local transaction.
    let replacement_write = document_for_shard(&topology, ShardId::from_number(1), 2);
    let op_three = "lane-op-3";
    let refused =
        lane.write(&topology, op_three, vec![replacement_write.clone()], &["m-a2", "m-b1"]);
    assert!(
        matches!(refused, ClusterOutcome::Refused { .. } | ClusterOutcome::Partial { .. }),
        "{refused:?}"
    );
    for member in lane.open_members() {
        assert!(
            !lane.holds_operation(member, op_three),
            "{op_three} landed nowhere while its shard had no owner"
        );
    }
    let survivor_write = document_for_shard(&topology, ShardId::from_number(2), 2);
    let op_four = "lane-op-4";
    let surviving = lane.write(&topology, op_four, vec![survivor_write], &["m-a2", "m-b1"]);
    assert!(matches!(surviving, ClusterOutcome::Atomic(_)), "{surviving:?}");

    // The Platform fails the shard over to its Region sibling at a raised
    // epoch; the engine validates the decision and fences the deposed
    // owner's placements.
    let next_view = lane_view()
        .with_owner_at_epoch("shard/1", "m-a2", 2)
        .expect("valid owner")
        .with_owner_at_epoch("shard/2", "m-b1", 1)
        .expect("valid owner");
    let plan =
        FailoverDecision::decide(&lane.topology, &loss, lane_table(), next_view).expect("decision");
    assert_eq!(plan.transfers().len(), 1);
    assert_eq!(plan.transfers()[0].to(), &ShardedLane::member_id("m-a2"));

    // The refused write re-drives under the new topology and commits —
    // its observable result is preserved, never silently dropped.
    let recovered =
        lane.write(plan.topology(), op_three, vec![replacement_write], &["m-a2", "m-b1"]);
    assert!(matches!(recovered, ClusterOutcome::Atomic(_)), "{recovered:?}");
    assert!(lane.holds_operation("m-a2", op_three));

    // Recovery truth per shard, observed from engine state.
    let probes = vec![
        (ShardId::from_number(1), vec![in_one.id().clone()]),
        (ShardId::from_number(2), vec![in_two.id().clone()]),
    ];
    let observations = lane.observe_recovery(plan.topology(), &probes);
    let report =
        ShardRecoveryReport::try_new(plan.topology(), &observations).expect("report assembles");
    assert!(report.recovered(ShardId::from_number(1)));
    assert!(report.recovered(ShardId::from_number(2)));

    with_faults(
        "leader-loss, healing",
        &format!("{}\n\n{}", plan.receipt().summary(), report.summary()),
    )
}

#[test]
fn lane_shard_owner_loss_failover_is_truthful_and_deterministic() {
    let first = shard_owner_loss_receipt("owner-loss-a");
    let second = shard_owner_loss_receipt("owner-loss-b");
    assert_eq!(first, second, "the scenario is deterministic across runs");
    assert!(first.contains("shard transferred: shard/1 from m-a1 epoch 1 to m-a2 epoch 2"));
    assert!(first.contains("shard lost: shard/1 owner m-a1 region r-alpha domain fd-r-alpha"));
    assert!(
        first.contains("faults: leader-loss, healing"),
        "the receipt names its faults in the W4 fault vocabulary: {first}"
    );
    println!("\n{first}\n");
}

// ---------------------------------------------------------------------------
// Scenario 2: whole-Region loss after acknowledged sharded writes, with
// per-shard RPO
// ---------------------------------------------------------------------------

fn region_loss_receipt(tag: &str) -> String {
    let mut lane = ShardedLane::new(tag);
    let topology = lane_topology();

    // op-1 on shard/1, held by every Member; op-2 on shard/2, held only by
    // its owner (the lattice's local-level truth).
    let in_one = document_for_shard(&topology, ShardId::from_number(1), 1);
    let in_two = document_for_shard(&topology, ShardId::from_number(2), 1);
    let op_one = "lane-op-1";
    let op_two = "lane-op-2";
    assert!(matches!(
        lane.write(&topology, op_one, vec![in_one.clone()], &["m-a1"]),
        ClusterOutcome::Atomic(_)
    ));
    assert!(matches!(
        lane.write(&topology, op_two, vec![in_two.clone()], &["m-b1"]),
        ClusterOutcome::Atomic(_)
    ));
    for to in ["m-a2", "m-b1", "m-b2", "m-g1", "m-g2"] {
        lane.deliver("m-a1", to);
    }
    assert!(lane.open_members().iter().all(|member| lane.holds_operation(member, op_one)));

    // r-alpha is lost entirely (both Members): shard/1 loses its owner and
    // both of its Region replicas, and op-1 survives on the other Regions.
    lane.close("m-a1");
    lane.close("m-a2");
    assert_eq!(rpo_of(&lane, &[op_one]), 0, "op-1 survives on r-beta and r-gamma");

    // r-beta is lost too: shard/2's owner goes with it, and its
    // owner-local op-2 is acknowledged-but-absent everywhere — a per-shard
    // RPO fact the scenario must state, not hide.
    lane.close("m-b1");
    lane.close("m-b2");
    assert_eq!(rpo_of(&lane, &[op_one]), 0);
    assert_eq!(rpo_of(&lane, &[op_two]), 1, "op-2 existed only on the lost Region");
    assert_eq!(rpo_of(&lane, &[op_one, op_two]), 1);

    // The Platform re-homes both shards into r-gamma at raised epochs; the
    // engine validates the double transfer.
    let loss = ShardLoss::assess(
        &lane.topology,
        &["m-a1", "m-a2", "m-b1", "m-b2"].map(ShardedLane::member_id),
    )
    .expect("assessment");
    let next_view = lane_view()
        .with_owner_at_epoch("shard/1", "m-g1", 2)
        .expect("valid owner")
        .with_owner_at_epoch("shard/2", "m-g2", 2)
        .expect("valid owner");
    let plan =
        FailoverDecision::decide(&lane.topology, &loss, lane_table(), next_view).expect("decision");
    assert_eq!(plan.transfers().len(), 2);

    // The surviving Members serve the re-homed shards.
    let after_one = document_for_shard(&topology, ShardId::from_number(1), 3);
    let after_two = document_for_shard(&topology, ShardId::from_number(2), 3);
    assert!(matches!(
        lane.write(plan.topology(), "lane-op-5", vec![after_one], &["m-g1", "m-g2"]),
        ClusterOutcome::Atomic(_)
    ));
    assert!(matches!(
        lane.write(plan.topology(), "lane-op-6", vec![after_two], &["m-g1", "m-g2"]),
        ClusterOutcome::Atomic(_)
    ));

    let probes = vec![
        (ShardId::from_number(1), vec![in_one.id().clone()]),
        (ShardId::from_number(2), vec![in_two.id().clone()]),
    ];
    let observations = lane.observe_recovery(plan.topology(), &probes);
    let report =
        ShardRecoveryReport::try_new(plan.topology(), &observations).expect("report assembles");
    assert!(report.recovered(ShardId::from_number(1)));
    assert!(
        report.unrecovered_shards().contains(&ShardId::from_number(2)),
        "shard/2's data died with its Region: the report says so, it does not claim recovery"
    );

    let mut receipt =
        with_faults("region-loss-after-acknowledged-write, healing", &plan.receipt().summary());
    receipt.push_str("\nrpo: 1 operation acknowledged then absent from every surviving Member (lane-op-2, shard/2)");
    receipt.push_str("\nrpo shard/1: 0 (lane-op-1 retained by the surviving Regions)");
    receipt.push_str("\nrecovery shard/2: the owner is re-seated and the shard serves again; its pre-loss data is lost with the Region");
    let _ = writeln!(receipt, "\n{}", report.summary());
    receipt
}

#[test]
fn lane_region_loss_after_acknowledged_sharded_writes_measures_per_shard_rpo() {
    let first = region_loss_receipt("region-loss-a");
    let second = region_loss_receipt("region-loss-b");
    assert_eq!(first, second, "the scenario is deterministic across runs");
    assert!(first.contains("rpo shard/1: 0"));
    assert!(first.contains("rpo: 1 operation"));
    assert!(
        first.contains("faults: region-loss-after-acknowledged-write, healing"),
        "the receipt names its faults in the W4 fault vocabulary: {first}"
    );
    println!("\n{first}\n");
}

// ---------------------------------------------------------------------------
// Scenario 3: a cross-shard cluster transaction across a lost shard
// ---------------------------------------------------------------------------

#[test]
fn lane_cross_shard_transaction_refuses_partially_and_completes_after_failover() {
    let mut lane = ShardedLane::new("cross-shard");
    let topology = lane_topology();
    let in_one = document_for_shard(&topology, ShardId::from_number(1), 1);
    let in_two = document_for_shard(&topology, ShardId::from_number(2), 1);
    let operation = "lane-cross-op-1";

    // shard/2's owner is lost; the cross-shard transaction drives through
    // the serving set that still has shard/1's owner.
    lane.close("m-b1");
    let partial = lane.write(&topology, operation, vec![in_one.clone(), in_two.clone()], &["m-a1"]);
    let ClusterOutcome::Partial { committed, refused, .. } = &partial else {
        panic!("expected a partial outcome, got {partial:?}");
    };
    assert_eq!(committed.len(), 1, "shard/1 committed before the refusal");
    assert_eq!(refused.shard(), ShardId::from_number(2));
    assert!(lane.holds_operation("m-a1", operation));
    assert!(
        !lane.holds_operation("m-a2", operation),
        "the committed shard's fact never crosses shards"
    );

    // Fail shard/2 over and re-drive the same operation identity.
    let loss =
        ShardLoss::assess(&lane.topology, &[ShardedLane::member_id("m-b1")]).expect("assessment");
    let next_view = lane_view()
        .with_owner_at_epoch("shard/1", "m-a1", 1)
        .expect("valid owner")
        .with_owner_at_epoch("shard/2", "m-b2", 2)
        .expect("valid owner");
    let plan =
        FailoverDecision::decide(&lane.topology, &loss, lane_table(), next_view).expect("decision");

    let completed = lane.write(
        plan.topology(),
        operation,
        vec![in_one.clone(), in_two.clone()],
        &["m-a1", "m-b2"],
    );
    let ClusterOutcome::PerShard { committed } = &completed else {
        panic!("expected a per-shard outcome, got {completed:?}");
    };
    assert_eq!(committed.len(), 2);
    assert!(committed[0].replayed(), "shard/1 replays its committed result");
    assert_eq!(
        committed[0].receipt().sequence(),
        partial_committed_sequence(&partial),
        "the observable commit result is preserved across the re-drive"
    );
    assert!(!committed[1].replayed(), "shard/2 commits fresh after its failover");
    assert!(lane.documents_of("m-b2").contains(in_two.id()));

    let receipt = with_faults("leader-loss, healing", &plan.receipt().summary());
    assert!(
        receipt.contains("faults: leader-loss, healing"),
        "the receipt names its faults in the W4 fault vocabulary: {receipt}"
    );
    println!("\n{receipt}\n");
}

/// Extracts the first committed receipt's sequence from a partial outcome.
fn partial_committed_sequence(outcome: &ClusterOutcome) -> crate::types::StorageSequence {
    let ClusterOutcome::Partial { committed, .. } = outcome else {
        panic!("expected a partial outcome");
    };
    committed[0].receipt().sequence()
}

// ---------------------------------------------------------------------------
// Scenario 4: a Member lost beyond its backup boundary, restored, and
// rejoined, with the recovery window reported per shard
// ---------------------------------------------------------------------------

#[test]
fn lane_backup_restore_rejoin_reports_the_recovery_window_per_shard() {
    let mut lane = ShardedLane::new("backup-rejoin");
    let topology = lane_topology();
    let shard = ShardId::from_number(2);

    // One write on shard/1 gives the report a probe for the untouched
    // shard.
    let untouched = document_for_shard(&topology, ShardId::from_number(1), 1);
    assert!(matches!(
        lane.write(&topology, "lane-op-0", vec![untouched.clone()], &["m-a1"]),
        ClusterOutcome::Atomic(_)
    ));

    // The owner commits op-1; a full backup is taken; then it commits
    // op-2, which exists only beyond the backup boundary.
    let first = document_for_shard(&topology, shard, 1);
    let second = document_for_shard(&topology, shard, 2);
    assert!(matches!(
        lane.write(&topology, "lane-op-1", vec![first.clone()], &["m-b1"]),
        ClusterOutcome::Atomic(_)
    ));
    let destination = location("backup-rejoin-dest");
    std::fs::remove_dir_all(&destination).ok();
    let backup =
        run_backup(lane.engine("m-b1"), BackupPlan::full(), &destination).expect("backup runs");
    assert_eq!(backup.manifest().documents(), 1, "the backup holds op-1's document");

    assert!(matches!(
        lane.write(&topology, "lane-op-2", vec![second.clone()], &["m-b1"]),
        ClusterOutcome::Atomic(_)
    ));

    // The Member is lost beyond its durable location: the location is
    // destroyed, then rebuilt from the backup, and the Member rejoins on
    // the restored location.
    lane.close("m-b1");
    let location_dir = lane.dir_of("m-b1");
    std::fs::remove_dir_all(&location_dir).expect("the location is lost");
    let receipt = RestorePlan::restore_latest(&destination, &location_dir).expect("restore runs");
    assert_eq!(receipt.restored_documents(), 1, "the restore rebuilds the backed-up state");
    let restored = Engine::open(
        EngineConfig::durable(&location_dir)
            .expect("durable location")
            .with_storage_backend(crate::engine::StorageBackend::RootFiles),
    )
    .expect("the restored location reopens");
    lane.replace_engine("m-b1", restored);

    // Recovery truth: the restored Member holds the backed-up document and
    // nothing beyond the backup boundary; no Member holds the tail.
    let held = lane.documents_of("m-b1");
    assert!(held.contains(first.id()), "the restored Member holds the backed-up document");
    assert!(!held.contains(second.id()), "the tail beyond the backup is gone");
    assert_eq!(
        rpo_of(&lane, &["lane-op-2"]),
        1,
        "the unreplicated tail is acknowledged-but-absent everywhere"
    );

    // The recovery report names every shard and Member with the verdict.
    let probes = vec![
        (ShardId::from_number(1), vec![untouched.id().clone()]),
        (shard, vec![first.id().clone()]),
    ];
    let observations = lane.observe_recovery(&lane.topology, &probes);
    let report = ShardRecoveryReport::try_new(&lane.topology, &observations).expect("report");
    assert!(report.recovered(shard), "the restored owner holds the shard's backed-up state");
    assert!(report.recovered(ShardId::from_number(1)));

    let mut rendered = with_faults("member-restart, member-rejoin", &report.summary());
    rendered.push_str("\nrecovery window: shard/2 sequences above the backup boundary are lost with the Member (lane-op-2)");
    rendered.push_str("\nlimit: the restored Member holds exactly the backup; the window above it is recoverable only through the replication plane");
    assert!(
        rendered.contains("faults: member-restart, member-rejoin"),
        "the receipt names its faults in the W4 fault vocabulary: {rendered}"
    );
    println!("\n{rendered}\n");
}
