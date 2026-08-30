//! The crate-private three-Region synthetic fault lane (W4-REPLICATION
//! R-3).
//!
//! The lane builds the contract's qualification topology — three Regions
//! `r-alpha`, `r-beta`, `r-gamma`; two Members per Region; each Region one
//! failure domain; every Member a real durable Engine in a temporary
//! directory — over the controlled in-process link doubles. It is
//! deterministic throughout: the only clock is the network's lane clock
//! (one tick per pump), and every fault is an armed link control or an
//! explicit member close. Nothing here is production machinery and nothing
//! here makes a production claim; the lane exists to run scenarios and
//! assemble secret-safe [`ReplicationReceipt`] artifacts.

use std::path::PathBuf;
use std::sync::atomic::{AtomicU64, Ordering};

use super::member::{
    MemberError, ReplicationMember, ReplicationWrite, observe_convergence, synchronize,
};
use super::transport::ReplicationNetwork;
use super::{
    AcknowledgementLevel, Convergence, MemberId, ReplicationConfig, ReplicationInstant,
    TopologyView,
};
use crate::engine::{CommitReceipt, DurableResolution, EngineConfig};
use crate::types::{
    CollectionName, Document, DocumentId, DocumentValue, OperationId, StorageSequence, TenantId,
    TenantScope,
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

/// The data range the lane's writes address.
pub(crate) const LANE_RANGE: &str = "main";

/// One lane temp directory: the test name and process id keep runs unique.
fn location(tag: &str) -> PathBuf {
    static COUNTER: AtomicU64 = AtomicU64::new(0);
    let index = COUNTER.fetch_add(1, Ordering::Relaxed);
    std::env::temp_dir().join(format!("terrabase-w4-lane-{tag}-{}-{index}", std::process::id()))
}

/// The lane's base topology: `m-a1` owns the range at epoch 1.
pub(crate) fn lane_topology() -> TopologyView {
    lane_topology_with_owner("m-a1", 1)
}

/// The lane's topology with `owner` holding the range at `epoch`: the
/// control-plane transition a scenario applies to confirm a new leader.
pub(crate) fn lane_topology_with_owner(owner: &str, epoch: u64) -> TopologyView {
    let mut view = TopologyView::new();
    for region in LANE_REGIONS {
        view = view.with_region(region).expect("valid region");
    }
    for (member, region) in LANE_MEMBERS {
        view = view.with_member(member, region, &format!("fd-{region}")).expect("valid member");
    }
    view.with_owner_at_epoch(LANE_RANGE, owner, epoch)
        .expect("valid owner")
        .validated()
        .expect("valid lane topology")
}

/// One acknowledgement fact the lane records for RPO accounting.
#[allow(dead_code, reason = "the composite scenario assembles its receipt ack facts from it")]
pub(crate) struct AckedOperation {
    /// The acknowledged operation identity.
    pub(crate) operation: OperationId,
    /// The level observed for it.
    pub(crate) level: AcknowledgementLevel,
    /// Its storage sequence at the acknowledging writer.
    pub(crate) sequence: StorageSequence,
    /// The Members that contributed.
    pub(crate) contributing: Vec<MemberId>,
}

/// The three-Region fault lane harness.
pub(crate) struct LaneHarness {
    dirs: Vec<PathBuf>,
    members: Vec<ReplicationMember>,
    /// The lane clock and links.
    pub(crate) network: ReplicationNetwork,
}

impl LaneHarness {
    /// Builds the lane: six durable Members over the base topology.
    pub(crate) fn new(tag: &str) -> Self {
        let topology = lane_topology();
        let config = ReplicationConfig::try_new(topology.clone()).expect("valid lane config");
        let mut dirs = Vec::new();
        let mut members = Vec::new();
        for (name, _) in LANE_MEMBERS {
            let dir = location(tag);
            std::fs::remove_dir_all(&dir).ok();
            let engine_config = EngineConfig::durable(&dir).expect("durable location");
            members.push(
                ReplicationMember::try_new(
                    config.clone(),
                    MemberId::try_new(name).expect("valid lane member"),
                    engine_config,
                )
                .expect("lane member opens"),
            );
            dirs.push(dir);
        }
        Self { dirs, members, network: ReplicationNetwork::new() }
    }

    /// Returns the lane's base topology view.
    #[allow(clippy::unused_self, reason = "one harness-bound seam for the scenario")]
    pub(crate) fn topology(&self) -> TopologyView {
        lane_topology()
    }

    #[allow(clippy::unused_self, reason = "name lookup reads the fixed lane roster")]
    fn index(&self, name: &str) -> usize {
        LANE_MEMBERS.iter().position(|(member, _)| *member == name).expect("lane member")
    }

    /// Returns one member by lane name.
    pub(crate) fn member(&self, name: &str) -> &ReplicationMember {
        let index = self.index(name);
        &self.members[index]
    }

    /// Returns one member by lane name, mutably.
    pub(crate) fn member_mut(&mut self, name: &str) -> &mut ReplicationMember {
        let index = self.index(name);
        &mut self.members[index]
    }

    /// Returns one member's validated lane identity.
    #[allow(clippy::unused_self, reason = "name lookup reads the fixed lane roster")]
    pub(crate) fn member_id(&self, name: &str) -> MemberId {
        MemberId::try_new(name).expect("valid lane member")
    }

    /// Builds the caller-supplied scope for one live member and tenant.
    ///
    /// Panics when the member's engine is closed: callers build scopes
    /// before closing a member, mirroring Platform's role.
    pub(crate) fn scope(&self, name: &str, tenant: &str) -> TenantScope {
        let engine = self.member(name).engine().expect("engine open");
        TenantScope::from_parts(TenantId::try_new(tenant).expect("valid tenant"), engine.identity())
    }

    /// Commits one document write on the named member and returns its
    /// receipt.
    pub(crate) fn write(
        &mut self,
        name: &str,
        tenant: &str,
        operation: &str,
        document: &str,
        text: &str,
    ) -> CommitReceipt {
        self.try_write(name, tenant, operation, document, text).expect("lane write commits")
    }

    /// Commits one document write on the named member, returning its typed
    /// failure instead of panicking.
    pub(crate) fn try_write(
        &mut self,
        name: &str,
        tenant: &str,
        operation: &str,
        document: &str,
        text: &str,
    ) -> Result<CommitReceipt, MemberError> {
        let scope = self.scope(name, tenant);
        let write = ReplicationWrite::new(
            OperationId::try_new(operation).expect("valid operation"),
            LANE_RANGE,
            AcknowledgementLevel::Local,
        )
        .expect("valid write request")
        .with_put(Document::new(
            DocumentId::try_new(document).expect("valid identifier"),
            CollectionName::try_new("users").expect("valid collection"),
            DocumentValue::String(text.to_owned()),
        ));
        self.member_mut(name).commit_write(&scope, write)
    }

    /// Drives bounded rounds of catch-up from `source` to `target`.
    pub(crate) fn replicate(
        &mut self,
        source: &str,
        target: &str,
        tenant: &str,
        rounds: usize,
    ) -> super::member::ApplyReport {
        let source_scope = self.scope(source, tenant);
        let target_scope = self.scope(target, tenant);
        let (i, j) = (self.index(source), self.index(target));
        assert_ne!(i, j, "a member does not replicate to itself");
        let (source_member, target_member) = if i < j {
            let (left, right) = self.members.split_at_mut(j);
            (&mut left[i], &mut right[0])
        } else {
            let (left, right) = self.members.split_at_mut(i);
            (&mut right[0], &mut left[j])
        };
        synchronize(
            &mut self.network,
            source_member,
            &source_scope,
            target_member,
            &target_scope,
            rounds,
        )
        .expect("lane replication runs")
    }

    /// Waits for one operation's acknowledgement at `level`, with every
    /// other live member as a peer and a deadline `ticks` pumps out.
    pub(crate) fn wait_ack(
        &mut self,
        writer: &str,
        tenant: &str,
        operation: &str,
        level: AcknowledgementLevel,
        ticks: u64,
    ) -> super::ReplicationOutcome {
        let writer_scope = self.scope(writer, tenant);
        let operation = OperationId::try_new(operation).expect("valid operation");
        let deadline = self.network.now().plus_ticks(ticks);
        let index = self.index(writer);
        let (before, rest) = self.members.split_at_mut(index);
        let (writer_slice, after) = rest.split_at_mut(1);
        let tenant_id = TenantId::try_new(tenant).expect("valid tenant");
        let mut peers: Vec<(&mut ReplicationMember, TenantScope)> = before
            .iter_mut()
            .chain(after.iter_mut())
            .filter(|peer| peer.engine().is_some())
            .map(|peer| {
                let identity = peer.engine().expect("engine open").identity();
                (peer, TenantScope::from_parts(tenant_id.clone(), identity))
            })
            .collect();
        writer_slice[0].wait_for_acknowledgement(
            &mut self.network,
            &writer_scope,
            &operation,
            level,
            &mut peers,
            deadline,
        )
    }

    /// Serves one member's catch-up requests under a scope-named tenant.
    pub(crate) fn serve_under(&mut self, name: &str, tenant: &str) -> Result<usize, MemberError> {
        let scope = self.scope(name, tenant);
        let index = self.index(name);
        self.members[index].serve_catch_up(&mut self.network, &scope)
    }

    /// Applies a membership view to one member (a control-plane
    /// transition).
    pub(crate) fn apply_view(&mut self, name: &str, view: TopologyView) {
        self.member_mut(name).apply_view(view).expect("lane view applies");
    }

    /// Closes one member's engine; the durable location keeps its bytes.
    pub(crate) fn close(&mut self, name: &str) {
        self.member_mut(name).close();
    }

    /// Restarts one member: engine closed and reopened from its directory.
    pub(crate) fn restart(&mut self, name: &str) {
        self.member_mut(name).restart().expect("lane member restarts");
    }

    /// Rejoins one member against the cursor it left with.
    pub(crate) fn rejoin_with(&mut self, name: &str, cursor: &super::ReplicationCursor) {
        self.member_mut(name).rejoin(cursor).expect("lane member rejoins");
    }

    /// Arms delay and reorder on one direction.
    pub(crate) fn delay_and_reorder(&mut self, from: &str, to: &str) {
        let link = self.network.link(&self.member_id(from), &self.member_id(to));
        link.delay_pumps(2);
        link.reorder();
    }

    /// Arms a bounded drop on one direction.
    pub(crate) fn drop_next(&mut self, from: &str, to: &str, frames: u32) {
        self.network.link(&self.member_id(from), &self.member_id(to)).drop_next(frames);
    }

    /// Arms duplication on one direction.
    pub(crate) fn duplicate(&mut self, from: &str, to: &str) {
        self.network.link(&self.member_id(from), &self.member_id(to)).duplicate();
    }

    /// Partitions one Region from both other Regions, in both directions.
    pub(crate) fn partition_region(&mut self, isolated: &str) {
        for (inside, inside_region) in LANE_MEMBERS {
            if inside_region == isolated {
                continue;
            }
            for (outside, outside_region) in LANE_MEMBERS {
                if outside_region != isolated {
                    continue;
                }
                let from = self.member_id(inside);
                let to = self.member_id(outside);
                self.network.link(&from, &to).partition();
                self.network.link(&to, &from).partition();
            }
        }
    }

    /// Heals every lane link.
    pub(crate) fn heal(&mut self) {
        for (from, _) in LANE_MEMBERS {
            for (to, _) in LANE_MEMBERS {
                if from == to {
                    continue;
                }
                self.network.link(&self.member_id(from), &self.member_id(to)).clear_faults();
            }
        }
    }

    /// Reports whether one member durably holds an operation; a closed
    /// member holds nothing observable.
    pub(crate) fn holds(&self, name: &str, tenant: &str, operation: &str) -> bool {
        let Some(engine) = self.member(name).engine() else { return false };
        let scope = self.scope(name, tenant);
        matches!(
            engine
                .resolve_commit(&scope, OperationId::try_new(operation).expect("valid operation")),
            Ok(DurableResolution::Committed(_))
        )
    }

    /// Returns the number of journaled commits one member holds for a
    /// tenant.
    pub(crate) fn journal_len(&self, name: &str, tenant: &str) -> usize {
        self.member(name)
            .engine()
            .expect("engine open")
            .tenant_commits(
                &TenantId::try_new(tenant).expect("valid tenant"),
                StorageSequence::new(0),
                1000,
            )
            .expect("journal read")
            .len()
    }

    /// Reads one tenant's documents through a member's Engine snapshot.
    pub(crate) fn documents(&self, name: &str, tenant: &str) -> Vec<Document> {
        let scope = self.scope(name, tenant);
        let engine = self.member(name).engine().expect("engine open");
        let snapshot = engine.read_snapshot(&scope).expect("snapshot");
        engine.snapshot_documents(&scope, &snapshot).expect("documents")
    }

    /// Observes convergence for one operation across the named members at
    /// an injected lane instant.
    pub(crate) fn observe(
        &self,
        operation: &str,
        names: &[&str],
        tenant: &str,
        at: ReplicationInstant,
    ) -> Convergence {
        let operation = OperationId::try_new(operation).expect("valid operation");
        let scopes: Vec<TenantScope> = names.iter().map(|name| self.scope(name, tenant)).collect();
        let observers: Vec<(&ReplicationMember, &TenantScope)> = names
            .iter()
            .zip(scopes.iter())
            .map(|(name, scope)| (self.member(name), scope))
            .collect();
        observe_convergence(&operation, &observers, at)
    }

    /// Measures the replication lag of one follower against one source: the
    /// storage-sequence distance between the follower's durable cursor high
    /// water and the source's journaled high water.
    pub(crate) fn lag(&self, source: &str, follower: &str) -> u64 {
        let source_high = self
            .member(source)
            .engine()
            .and_then(|engine| engine.journaled_high_water().ok())
            .flatten()
            .map_or(0, StorageSequence::get);
        let follower_high = self
            .member(follower)
            .cursor()
            .ok()
            .and_then(|cursor| cursor.high_water())
            .map_or(0, StorageSequence::get);
        source_high.saturating_sub(follower_high)
    }

    /// Measures the RPO over the recorded acknowledgement facts: the count
    /// of acknowledged operations absent from every surviving member, and
    /// their storage-sequence window.
    pub(crate) fn rpo(
        &self,
        tenant: &str,
        acknowledged: &[AckedOperation],
        survivors: &[&str],
    ) -> (usize, Option<(StorageSequence, StorageSequence)>) {
        let lost: Vec<StorageSequence> = acknowledged
            .iter()
            .filter(|acked| {
                !survivors.iter().any(|name| self.holds(name, tenant, acked.operation.as_str()))
            })
            .map(|acked| acked.sequence)
            .collect();
        if lost.is_empty() {
            return (0, None);
        }
        let low = lost.iter().min().copied().expect("nonempty");
        let high = lost.iter().max().copied().expect("nonempty");
        (lost.len(), Some((low, high)))
    }

    /// Closes every member and removes the lane's temporary directories.
    pub(crate) fn clean_up(mut self) {
        for member in &mut self.members {
            member.close();
        }
        for dir in &self.dirs {
            std::fs::remove_dir_all(dir).ok();
        }
    }
}
