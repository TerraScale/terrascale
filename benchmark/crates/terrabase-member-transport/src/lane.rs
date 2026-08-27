//! The three-Region SE-06 lane harness over engine-owned transports.
//!
//! The lane builds the contract's qualification topology — three Regions
//! `r-alpha`, `r-beta`, `r-gamma`; two Members per Region; each Region one
//! failure domain; every Member a real durable journal in a temporary
//! directory — over any [`MemberTransport`]: the in-process lane where
//! Members share a process boundary, or the engine-socket lane (QUIC) for
//! networked links. It is the W4 R-3 lane re-run on real transports: the
//! same topology, the same fault vocabulary, the same composite scenario
//! script, and lane-scoped [`LaneReceipt`] artifacts.
//!
//! Non-claims: measured values are lane-scoped artifacts reported by the
//! tests, never claims beyond the lane; no production topology, timing, or
//! performance capability is implied by anything here.

use std::path::PathBuf;
use std::sync::atomic::{AtomicU64, Ordering};

use crate::envelope::LaneDocument;
use crate::identity::{CollectionName, DocumentId, MemberId, OperationId, TenantId, TopologyView};
use crate::journal::LaneCursor;
use crate::member::{
    AcknowledgementLevel, CommitReceipt, Convergence, FaultClass, LaneConfig, LaneMember,
    LaneWriteRequest, MemberError, ReplicationError, ReplicationOutcome,
    dispatch_report_deliveries, level_satisfied, normalize_level, observe_convergence,
    strongest_satisfied_level,
};
use crate::receipt::{BytesOnWire, LaneKind, LaneReceipt};
use crate::transport::MemberTransport;

/// The lane's Regions, in canonical order.
pub const LANE_REGIONS: [&str; 3] = ["r-alpha", "r-beta", "r-gamma"];

/// The lane's Members and their Region seats, in canonical order.
pub const LANE_MEMBERS: [(&str, &str); 6] = [
    ("m-a1", "r-alpha"),
    ("m-a2", "r-alpha"),
    ("m-b1", "r-beta"),
    ("m-b2", "r-beta"),
    ("m-g1", "r-gamma"),
    ("m-g2", "r-gamma"),
];

/// The data range the lane's writes address.
pub const LANE_RANGE: &str = "main";

/// Returns one validated lane member identity by name.
fn lane_member_id(name: &str) -> MemberId {
    MemberId::try_new(name).expect("valid lane member")
}

/// One lane temp directory: the test name and process id keep runs unique.
fn location(tag: &str) -> PathBuf {
    static COUNTER: AtomicU64 = AtomicU64::new(0);
    let index = COUNTER.fetch_add(1, Ordering::Relaxed);
    std::env::temp_dir().join(format!("tbv2-se06-lane-{tag}-{}-{index}", std::process::id()))
}

/// # Panics
///
/// Panics when a lane step fails its fixed expectation: the harness is
/// a deterministic qualification script, not a recovery surface — names
/// must be part of the fixed lane roster and every scripted step must
/// succeed.
/// The lane's base topology: `m-a1` owns the range at epoch 1.
#[must_use]
pub fn lane_topology() -> TopologyView {
    lane_topology_with_owner("m-a1", 1)
}

/// # Panics
///
/// Panics when a lane step fails its fixed expectation: the harness is
/// a deterministic qualification script, not a recovery surface — names
/// must be part of the fixed lane roster and every scripted step must
/// succeed.
/// The lane's topology with `owner` holding the range at `epoch`: the
/// control-plane transition a scenario applies to confirm a new leader.
#[must_use]
pub fn lane_topology_with_owner(owner: &str, epoch: u64) -> TopologyView {
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
pub struct AckedOperation {
    /// The acknowledged operation identity.
    pub operation: OperationId,
    /// The level observed for it.
    pub level: AcknowledgementLevel,
    /// Its journal sequence at the acknowledging writer.
    pub sequence: u64,
    /// The Members that contributed.
    pub contributing: Vec<MemberId>,
}

/// The three-Region fault lane harness over one engine-owned transport.
pub struct LaneHarness<T: MemberTransport> {
    dirs: Vec<PathBuf>,
    members: Vec<LaneMember>,
    /// The lane clock and links.
    pub transport: T,
    deliveries: u64,
}

impl<T: MemberTransport> LaneHarness<T> {
    /// # Panics
    ///
    /// Panics when a lane step fails its fixed expectation: the harness is
    /// a deterministic qualification script, not a recovery surface — names
    /// must be part of the fixed lane roster and every scripted step must
    /// succeed.
    /// Builds the lane: six durable Members over the base topology.
    pub fn new(tag: &str, transport: T) -> Self {
        let topology = lane_topology();
        let config = LaneConfig::try_new(topology.clone()).expect("valid lane config");
        let mut dirs = Vec::new();
        let mut members = Vec::new();
        for (name, _) in LANE_MEMBERS {
            let dir = location(tag);
            std::fs::remove_dir_all(&dir).ok();
            members.push(
                LaneMember::try_new(
                    config.clone(),
                    MemberId::try_new(name).expect("valid lane member"),
                    &dir,
                )
                .expect("lane member opens"),
            );
            dirs.push(dir);
        }
        Self { dirs, members, transport, deliveries: 0 }
    }

    /// # Panics
    ///
    /// Panics when a lane step fails its fixed expectation: the harness is
    /// a deterministic qualification script, not a recovery surface — names
    /// must be part of the fixed lane roster and every scripted step must
    /// succeed.
    /// Returns the lane's base topology view.
    #[allow(clippy::unused_self, reason = "one harness-bound seam for the scenario")]
    #[must_use]
    pub fn topology(&self) -> TopologyView {
        lane_topology()
    }

    #[allow(clippy::unused_self, reason = "name lookup reads the fixed lane roster")]
    fn index(&self, name: &str) -> usize {
        LANE_MEMBERS.iter().position(|(member, _)| *member == name).expect("lane member")
    }

    /// # Panics
    ///
    /// Panics when a lane step fails its fixed expectation: the harness is
    /// a deterministic qualification script, not a recovery surface — names
    /// must be part of the fixed lane roster and every scripted step must
    /// succeed.
    /// Returns one member by lane name.
    #[must_use]
    pub fn member(&self, name: &str) -> &LaneMember {
        let index = self.index(name);
        &self.members[index]
    }

    /// # Panics
    ///
    /// Panics when a lane step fails its fixed expectation: the harness is
    /// a deterministic qualification script, not a recovery surface — names
    /// must be part of the fixed lane roster and every scripted step must
    /// succeed.
    /// Returns one member by lane name, mutably.
    #[must_use]
    pub fn member_mut(&mut self, name: &str) -> &mut LaneMember {
        let index = self.index(name);
        &mut self.members[index]
    }

    /// # Panics
    ///
    /// Panics when a lane step fails its fixed expectation: the harness is
    /// a deterministic qualification script, not a recovery surface — names
    /// must be part of the fixed lane roster and every scripted step must
    /// succeed.
    /// Returns one member's validated lane identity.
    #[allow(clippy::unused_self, reason = "name lookup reads the fixed lane roster")]
    #[must_use]
    pub fn member_id(&self, name: &str) -> MemberId {
        MemberId::try_new(name).expect("valid lane member")
    }

    /// # Panics
    ///
    /// Panics when a lane step fails its fixed expectation: the harness is
    /// a deterministic qualification script, not a recovery surface — names
    /// must be part of the fixed lane roster and every scripted step must
    /// succeed.
    /// Commits one document write on the named member and returns its
    /// receipt.
    pub fn write(
        &mut self,
        name: &str,
        tenant: &str,
        operation: &str,
        document: &str,
        text: &str,
    ) -> CommitReceipt {
        self.try_write(name, tenant, operation, document, text).expect("lane write commits")
    }

    /// # Panics
    ///
    /// Panics when a lane step fails its fixed expectation: the harness is
    /// a deterministic qualification script, not a recovery surface — names
    /// must be part of the fixed lane roster and every scripted step must
    /// succeed.
    /// Commits one document write on the named member, returning its typed
    /// failure instead of panicking.
    ///
    /// # Errors
    ///
    /// Returns the member's typed failures.
    pub fn try_write(
        &mut self,
        name: &str,
        tenant: &str,
        operation: &str,
        document: &str,
        text: &str,
    ) -> Result<CommitReceipt, MemberError> {
        let request = LaneWriteRequest::new(
            OperationId::try_new(operation).expect("valid operation"),
            LANE_RANGE,
        )
        .expect("valid write request")
        .with_put(LaneDocument {
            id: DocumentId::try_new(document).expect("valid identifier"),
            collection: CollectionName::try_new("users").expect("valid collection"),
            value: text.to_owned(),
        });
        let member = self.member_mut(name);
        member.commit_write(&TenantId::try_new(tenant).expect("valid tenant"), request)
    }

    /// # Panics
    ///
    /// Panics when a lane step fails its fixed expectation: the harness is
    /// a deterministic qualification script, not a recovery surface — names
    /// must be part of the fixed lane roster and every scripted step must
    /// succeed.
    /// Drives bounded rounds of the deterministic catch-up exchange from
    /// `source` to `target` until the follower reaches the source's
    /// last-known high water.
    pub fn replicate(
        &mut self,
        source: &str,
        target: &str,
        tenant: &str,
        rounds: usize,
    ) -> crate::member::DispatchReport {
        let tenant = TenantId::try_new(tenant).expect("valid tenant");
        let source_index = self.index(source);
        let target_index = self.index(target);
        let source_id = self.member_id(source);
        let mut report = crate::member::DispatchReport::default();
        for _ in 0..rounds {
            self.members[target_index]
                .request_catch_up(&mut self.transport, &source_id, &tenant)
                .expect("request sends");
            self.transport.pump();
            self.members[source_index]
                .dispatch_under(&mut self.transport, &tenant)
                .expect("source serves");
            self.transport.pump();
            report = self.members[target_index]
                .dispatch_under(&mut self.transport, &tenant)
                .expect("target applies");
            self.deliveries += dispatch_report_deliveries(&report);
            if report.caught_up {
                return report;
            }
        }
        report
    }

    /// # Panics
    ///
    /// Panics when a lane step fails its fixed expectation: the harness is
    /// a deterministic qualification script, not a recovery surface — names
    /// must be part of the fixed lane roster and every scripted step must
    /// succeed.
    /// Waits for one operation's acknowledgement at `level`, with every
    /// other live member as a peer and a deadline `ticks` pumps out.
    ///
    /// This is the D-2 wait: the local commit already happened; each round
    /// pushes the operation to the peers, pumps the lane, lets each peer
    /// apply and answer, and collects the reports. When the level is met the
    /// outcome names every contributing Region and Member; when the deadline
    /// arrives first it is a `Timeout` carrying the partial acknowledgement;
    /// when a lost Member makes the level unreachable it is a typed
    /// `Failed`.
    pub fn wait_ack(
        &mut self,
        writer: &str,
        tenant: &str,
        operation: &str,
        level: AcknowledgementLevel,
        ticks: u64,
    ) -> ReplicationOutcome {
        let outcomes = self.wait_ack_batch(writer, tenant, &[operation], level, ticks);
        outcomes
            .into_iter()
            .next()
            .map(|(_, outcome)| outcome)
            .expect("one operation yields one outcome")
    }

    /// # Panics
    ///
    /// Panics when a lane step fails its fixed expectation: the harness is
    /// a deterministic qualification script, not a recovery surface — names
    /// must be part of the fixed lane roster and every scripted step must
    /// succeed.
    /// Waits for every named operation's acknowledgement at `level`, with
    /// every other live member as a peer and a deadline `ticks` pumps out
    /// (SE-07 budgets E-3/E-4: each round pushes the whole outstanding set
    /// to each peer in one frame, and each peer answers in one batched
    /// report frame).
    ///
    /// The per-operation semantics are exactly [`LaneHarness::wait_ack`]'s;
    /// only the frame economy changes. The returned outcomes pair each
    /// named operation with its result.
    #[allow(clippy::too_many_lines, reason = "one fixed wait loop over the D-2 semantics")]
    pub fn wait_ack_batch(
        &mut self,
        writer: &str,
        tenant: &str,
        operations: &[&str],
        level: AcknowledgementLevel,
        ticks: u64,
    ) -> Vec<(String, ReplicationOutcome)> {
        let tenant = TenantId::try_new(tenant).expect("valid tenant");
        let operations: Vec<OperationId> = operations
            .iter()
            .map(|operation| OperationId::try_new(operation).expect("valid operation"))
            .collect();
        let writer_id = self.member_id(writer);
        let writer_index = self.index(writer);
        let deadline = self.transport.now().plus_ticks(ticks);
        let region_count = lane_topology().regions().count();
        let (canonical, normalization) = normalize_level(level, region_count);
        let mut resolved: Vec<(String, ReplicationOutcome)> = Vec::new();
        loop {
            let now = self.transport.now();
            let mut outstanding: Vec<OperationId> = Vec::new();
            for operation in &operations {
                if resolved.iter().any(|(name, _)| name == operation.as_str()) {
                    continue;
                }
                outstanding.push(operation.clone());
            }
            if outstanding.is_empty() {
                return resolved;
            }
            {
                let writer_member = &mut self.members[writer_index];
                writer_member.observe_instant(now);
                if !writer_member.is_open() {
                    return Self::fail_all(resolved, outstanding, &ReplicationError::JournalClosed);
                }
            }
            // Dispatch the writer first so late reports are collected.
            let writer_report = self.members[writer_index]
                .dispatch_under(&mut self.transport, &tenant)
                .expect("writer drains");
            self.deliveries += dispatch_report_deliveries(&writer_report);
            let topology = lane_topology();
            let mut next_outstanding = Vec::new();
            for operation in &outstanding {
                if !matches!(
                    self.members[writer_index].journal().resolve(operation),
                    Ok(crate::journal::DurableResolution::Committed(_))
                ) {
                    let name = operation.as_str().to_owned();
                    resolved
                        .push((name, ReplicationOutcome::Failed(ReplicationError::NotReplicated)));
                    continue;
                }
                let contributing = self.members[writer_index].ack_contributors(operation);
                if level_satisfied(canonical, &topology, &writer_id, &contributing) {
                    let report = self.members[writer_index].build_ack_report(
                        level,
                        canonical,
                        normalization,
                        &contributing,
                        self.transport.now(),
                    );
                    let name = operation.as_str().to_owned();
                    resolved.push((name, ReplicationOutcome::Acknowledged(report)));
                    continue;
                }
                next_outstanding.push(operation.clone());
            }
            let outstanding = next_outstanding;
            if outstanding.is_empty() {
                continue;
            }
            // Reachability: could the level ever be met by the Members that
            // are still alive? If not, the wait fails with the typed
            // category rather than burning the deadline.
            let mut possible = std::collections::BTreeSet::from([writer_id.clone()]);
            for (index, (member, _)) in LANE_MEMBERS.iter().enumerate() {
                if *member != writer && self.members[index].is_open() {
                    possible.insert(self.member_id(member));
                }
            }
            if !level_satisfied(canonical, &topology, &writer_id, &possible) {
                let error = if level == AcknowledgementLevel::AllRegions {
                    ReplicationError::MemberUnavailable
                } else {
                    ReplicationError::QuorumUnreachable
                };
                return Self::fail_all(resolved, outstanding, &error);
            }
            if self.transport.now() >= deadline {
                for operation in outstanding {
                    let contributing = self.members[writer_index].ack_contributors(&operation);
                    let strongest = strongest_satisfied_level(&topology, &writer_id, &contributing);
                    let (partial, _) = normalize_level(strongest, region_count);
                    let observed = self.members[writer_index].build_ack_report(
                        strongest,
                        partial,
                        None,
                        &contributing,
                        self.transport.now(),
                    );
                    let name = operation.as_str().to_owned();
                    resolved.push((
                        name,
                        ReplicationOutcome::Timeout {
                            operation_id: operation,
                            requested: level,
                            observed,
                        },
                    ));
                }
                return resolved;
            }
            // One deterministic round: one batched push per peer, pump,
            // peers apply and answer, pump, writer collects.
            for (index, (member, _)) in LANE_MEMBERS.iter().enumerate() {
                if *member == writer || !self.members[index].is_open() {
                    continue;
                }
                let peer_id = self.member_id(member);
                let pushed = self.members[writer_index]
                    .send_push_batch(&mut self.transport, &peer_id, &tenant, &outstanding)
                    .expect("push sends");
                assert!(pushed == outstanding.len(), "every outstanding operation is pushed");
            }
            self.transport.pump();
            for (index, (member, _)) in LANE_MEMBERS.iter().enumerate() {
                if *member == writer || !self.members[index].is_open() {
                    continue;
                }
                let peer_report = self.members[index]
                    .dispatch_under(&mut self.transport, &tenant)
                    .expect("peer dispatches");
                self.deliveries += dispatch_report_deliveries(&peer_report);
            }
            self.transport.pump();
        }
    }

    /// Fails every outstanding operation with the typed error.
    fn fail_all(
        mut resolved: Vec<(String, ReplicationOutcome)>,
        outstanding: Vec<OperationId>,
        error: &ReplicationError,
    ) -> Vec<(String, ReplicationOutcome)> {
        for operation in outstanding {
            resolved
                .push((operation.as_str().to_owned(), ReplicationOutcome::Failed(error.clone())));
        }
        resolved
    }

    /// # Panics
    ///
    /// Panics when a lane step fails its fixed expectation: the harness is
    /// a deterministic qualification script, not a recovery surface — names
    /// must be part of the fixed lane roster and every scripted step must
    /// succeed.
    /// Applies a membership view to one member (a control-plane
    /// transition).
    pub fn apply_view(&mut self, name: &str, view: TopologyView) {
        self.member_mut(name).apply_view(view).expect("lane view applies");
    }

    /// # Panics
    ///
    /// Panics when a lane step fails its fixed expectation: the harness is
    /// a deterministic qualification script, not a recovery surface — names
    /// must be part of the fixed lane roster and every scripted step must
    /// succeed.
    /// Closes one member's journal; the durable location keeps its bytes.
    pub fn close(&mut self, name: &str) {
        self.member_mut(name).close();
    }

    /// # Panics
    ///
    /// Panics when a lane step fails its fixed expectation: the harness is
    /// a deterministic qualification script, not a recovery surface — names
    /// must be part of the fixed lane roster and every scripted step must
    /// succeed.
    /// Restarts one member: journal closed and reopened from its directory.
    pub fn restart(&mut self, name: &str) {
        self.member_mut(name).restart().expect("lane member restarts");
    }

    /// # Panics
    ///
    /// Panics when a lane step fails its fixed expectation: the harness is
    /// a deterministic qualification script, not a recovery surface — names
    /// must be part of the fixed lane roster and every scripted step must
    /// succeed.
    /// Rejoins one member against the cursor it left with.
    pub fn rejoin_with(&mut self, name: &str, cursor: &LaneCursor) {
        self.member_mut(name).rejoin(cursor).expect("lane member rejoins");
    }

    /// # Panics
    ///
    /// Panics when a lane step fails its fixed expectation: the harness is
    /// a deterministic qualification script, not a recovery surface — names
    /// must be part of the fixed lane roster and every scripted step must
    /// succeed.
    /// Arms delay and reorder on one direction.
    pub fn delay_and_reorder(&mut self, from: &str, to: &str) {
        let link = self.transport.link(&self.member_id(from), &self.member_id(to));
        link.delay_pumps(2);
        link.reorder();
    }

    /// # Panics
    ///
    /// Panics when a lane step fails its fixed expectation: the harness is
    /// a deterministic qualification script, not a recovery surface — names
    /// must be part of the fixed lane roster and every scripted step must
    /// succeed.
    /// Arms a bounded drop on one direction.
    pub fn drop_next(&mut self, from: &str, to: &str, frames: u32) {
        self.transport.link(&self.member_id(from), &self.member_id(to)).drop_next(frames);
    }

    /// # Panics
    ///
    /// Panics when a lane step fails its fixed expectation: the harness is
    /// a deterministic qualification script, not a recovery surface — names
    /// must be part of the fixed lane roster and every scripted step must
    /// succeed.
    /// Arms duplication on one direction.
    pub fn duplicate(&mut self, from: &str, to: &str) {
        self.transport.link(&self.member_id(from), &self.member_id(to)).duplicate();
    }

    /// # Panics
    ///
    /// Panics when a lane step fails its fixed expectation: the harness is
    /// a deterministic qualification script, not a recovery surface — names
    /// must be part of the fixed lane roster and every scripted step must
    /// succeed.
    /// Partitions one Region from both other Regions, in both directions.
    pub fn partition_region(&mut self, isolated: &str) {
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
                self.transport.link(&from, &to).partition();
                self.transport.link(&to, &from).partition();
            }
        }
    }

    /// # Panics
    ///
    /// Panics when a lane step fails its fixed expectation: the harness is
    /// a deterministic qualification script, not a recovery surface — names
    /// must be part of the fixed lane roster and every scripted step must
    /// succeed.
    /// Heals every lane link.
    pub fn heal(&mut self) {
        self.transport.heal();
    }

    /// # Panics
    ///
    /// Panics when a lane step fails its fixed expectation: the harness is
    /// a deterministic qualification script, not a recovery surface — names
    /// must be part of the fixed lane roster and every scripted step must
    /// succeed.
    /// Reports whether one member durably holds an operation; a closed
    /// member holds nothing observable.
    #[must_use]
    pub fn holds(&self, name: &str, _tenant: &str, operation: &str) -> bool {
        let member = self.member(name);
        if !member.is_open() {
            return false;
        }
        matches!(
            member.journal().resolve(&OperationId::try_new(operation).expect("valid operation")),
            Ok(crate::journal::DurableResolution::Committed(_))
        )
    }

    /// # Panics
    ///
    /// Panics when a lane step fails its fixed expectation: the harness is
    /// a deterministic qualification script, not a recovery surface — names
    /// must be part of the fixed lane roster and every scripted step must
    /// succeed.
    /// Returns the number of journaled commits one member holds for a
    /// tenant.
    #[must_use]
    pub fn journal_len(&self, name: &str, tenant: &str) -> usize {
        self.member(name)
            .journal()
            .tenant_commits(&TenantId::try_new(tenant).expect("valid tenant"), 0, u32::MAX / 2)
            .expect("journal read")
            .len()
    }

    /// # Panics
    ///
    /// Panics when a lane step fails its fixed expectation: the harness is
    /// a deterministic qualification script, not a recovery surface — names
    /// must be part of the fixed lane roster and every scripted step must
    /// succeed.
    /// Reads one tenant's documents through a member's journal state.
    #[must_use]
    pub fn documents(&self, name: &str, tenant: &str) -> Vec<LaneDocument> {
        self.member(name)
            .journal()
            .documents_at(&TenantId::try_new(tenant).expect("valid tenant"))
            .expect("journal read")
    }

    /// # Panics
    ///
    /// Panics when a lane step fails its fixed expectation: the harness is
    /// a deterministic qualification script, not a recovery surface — names
    /// must be part of the fixed lane roster and every scripted step must
    /// succeed.
    /// Observes convergence for one operation across the named members at
    /// the current lane instant. The tenant names the scope the observation
    /// was driven under; the holding check itself is journal-derived.
    #[must_use]
    pub fn observe(&self, operation: &str, names: &[&str], _tenant: &str) -> Convergence {
        let operation = OperationId::try_new(operation).expect("valid operation");
        let observers: Vec<&LaneMember> = names.iter().map(|name| self.member(name)).collect();
        observe_convergence(&operation, &observers, self.transport.now())
    }

    /// # Panics
    ///
    /// Panics when a lane step fails its fixed expectation: the harness is
    /// a deterministic qualification script, not a recovery surface — names
    /// must be part of the fixed lane roster and every scripted step must
    /// succeed.
    /// Measures the replication lag of one follower against one source: the
    /// journal-sequence distance between the follower's durable cursor high
    /// water and the source's journaled high water.
    #[must_use]
    pub fn lag(&self, source: &str, follower: &str) -> u64 {
        let source_high =
            self.member(source).journal().journaled_high_water().ok().flatten().unwrap_or(0);
        let follower_high =
            self.member(follower).cursor().ok().and_then(|cursor| cursor.high_water()).unwrap_or(0);
        source_high.saturating_sub(follower_high)
    }

    /// # Panics
    ///
    /// Panics when a lane step fails its fixed expectation: the harness is
    /// a deterministic qualification script, not a recovery surface — names
    /// must be part of the fixed lane roster and every scripted step must
    /// succeed.
    /// Measures the RPO over the recorded acknowledgement facts: the count
    /// of acknowledged operations absent from every surviving member, and
    /// their journal-sequence window.
    #[must_use]
    pub fn rpo(
        &self,
        tenant: &str,
        acknowledged: &[AckedOperation],
        survivors: &[&str],
    ) -> (usize, Option<(u64, u64)>) {
        let lost: Vec<u64> = acknowledged
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

    /// # Panics
    ///
    /// Panics when a lane step fails its fixed expectation: the harness is
    /// a deterministic qualification script, not a recovery surface — names
    /// must be part of the fixed lane roster and every scripted step must
    /// succeed.
    /// Assembles the lane's bytes-on-wire accounting from the transport's
    /// per-class counters and the counted deliveries, checked against the
    /// written PD-13 D1 budgets.
    #[must_use]
    pub fn bytes_on_wire(&self) -> BytesOnWire {
        use crate::envelope::{BUDGET_BYTES_PER_DELIVERED_CHANGE, BUDGET_ENVELOPE_OVERHEAD_BYTES};
        let per_class: Vec<(&'static str, crate::receipt::FrameClassBytes)> = self
            .transport
            .class_bytes()
            .into_iter()
            .map(|(kind, bytes)| {
                (
                    kind.name(),
                    crate::receipt::FrameClassBytes {
                        frames: bytes.frames,
                        payload_bytes: bytes.payload_bytes,
                        total_bytes: bytes.total_bytes,
                        overhead_bytes: bytes.total_bytes - bytes.payload_bytes,
                    },
                )
            })
            .collect();
        let overhead_within_budget = per_class.iter().all(|(_, bytes)| {
            bytes.frames == 0
                || bytes
                    .overhead_bytes
                    .checked_div(bytes.frames)
                    .is_some_and(|overhead| overhead <= BUDGET_ENVELOPE_OVERHEAD_BYTES as u64)
        });
        let delivered_payload: u64 =
            self.transport.class_bytes().into_iter().map(|(_, bytes)| bytes.payload_bytes).sum();
        let per_change_within_budget = delivered_payload
            .checked_div(self.deliveries)
            .is_none_or(|per_change| per_change <= BUDGET_BYTES_PER_DELIVERED_CHANGE as u64);
        BytesOnWire {
            per_class,
            changes_delivered: self.deliveries,
            overhead_within_budget,
            per_change_within_budget,
        }
    }

    /// # Panics
    ///
    /// Panics when a lane step fails its fixed expectation: the harness is
    /// a deterministic qualification script, not a recovery surface — names
    /// must be part of the fixed lane roster and every scripted step must
    /// succeed.
    /// Closes every member and removes the lane's temporary directories.
    pub fn clean_up(mut self)
    where
        T: Sized,
    {
        for member in &mut self.members {
            member.close();
        }
        self.transport.heal();
        for dir in &self.dirs {
            std::fs::remove_dir_all(dir).ok();
        }
    }
}

/// # Panics
///
/// Panics when a lane step fails its fixed expectation: the harness is
/// a deterministic qualification script, not a recovery surface — names
/// must be part of the fixed lane roster and every scripted step must
/// succeed.
/// Runs the composite three-Region lane scenario and returns its receipt.
///
/// The script is the W4 composite, re-run over the caller's transport:
/// healthy cross-Region acknowledgement, a lag observation, link faults
/// with repair, a member restart and rejoin, a full two-Region partition
/// with isolated concurrent writes under divergent ownership views, an
/// acknowledgement at Region quorum inside the surviving Region, the loss
/// of the entire r-alpha Region (leader loss), recovery to a confirmed new
/// leader, healing, and the convergence observation naming every surviving
/// Member.
#[allow(clippy::too_many_lines, reason = "one fixed lane script, one act per fault class")]
pub fn run_composite_lane<T: MemberTransport>(
    tag: &str,
    lane: LaneKind,
    transport: T,
    ack_ticks: u64,
) -> LaneReceipt {
    let mut h = LaneHarness::new(tag, transport);
    let mut faults = Vec::new();
    let tenant = "tenant-lane";

    // Act A: healthy links, cross-Region acknowledgement at AllRegions.
    h.write("m-a1", tenant, "op-1", "doc-1", "lane-value-one");
    let acked_op1 =
        match h.wait_ack("m-a1", tenant, "op-1", AcknowledgementLevel::AllRegions, ack_ticks) {
            ReplicationOutcome::Acknowledged(report) => {
                assert_eq!(report.contributed.len(), 3, "every Region contributed");
                (OperationId::try_new("op-1").expect("valid id"), report)
            }
            other => panic!("the healthy lane acknowledges at AllRegions: {other:?}"),
        };

    // Act B: the lag observation — one unreplicated write leaves every
    // non-writer exactly one commit behind the source.
    h.write("m-a1", tenant, "op-2", "doc-2", "lane-value-two");
    let lag_distances: Vec<(MemberId, u64)> = ["m-a2", "m-b1", "m-b2", "m-g1", "m-g2"]
        .iter()
        .map(|name| (h.member_id(name), h.lag("m-a1", name)))
        .collect();

    // Act C: link faults — delay and reorder, drop, and duplicate — then
    // repair and heal.
    h.delay_and_reorder("m-a1", "m-b2");
    h.drop_next("m-a1", "m-b1", 1);
    h.duplicate("m-a1", "m-g1");
    for target in ["m-b1", "m-b2", "m-g1"] {
        let report = h.replicate("m-a1", target, tenant, 8);
        assert!(report.caught_up, "repair reaches {target} under the link faults");
    }
    for fault in [
        FaultClass::LinkDelay,
        FaultClass::LinkReorder,
        FaultClass::LinkDrop,
        FaultClass::LinkDuplicate,
        FaultClass::DuplicateReplay,
        FaultClass::Healing,
    ] {
        faults.push(fault);
    }
    h.heal();

    // Act D: member restart (journal closed and reopened from its
    // directory) and rejoin with cursor validation, then catch-up without
    // duplicates.
    let journal_before = h.journal_len("m-b2", tenant);
    let cursor_before = h.member("m-b2").cursor().expect("cursor derives from the journal");
    h.restart("m-b2");
    h.rejoin_with("m-b2", &cursor_before);
    let report = h.replicate("m-a1", "m-b2", tenant, 8);
    assert!(report.caught_up);
    assert_eq!(
        h.journal_len("m-b2", tenant),
        journal_before,
        "the rejoined member applies no duplicate"
    );
    faults.push(FaultClass::MemberRestart);
    faults.push(FaultClass::MemberRejoin);

    // Act E: full Region partition (r-beta and r-gamma isolated) with
    // isolated concurrent writes under divergent ownership views. Commits
    // stop for the non-owner until a new leader is confirmed by a
    // transitioned view.
    h.partition_region("r-beta");
    h.partition_region("r-gamma");
    let stopped = h.try_write("m-b1", tenant, "op-stopped", "doc-stopped", "never-commits");
    assert!(
        matches!(stopped, Err(MemberError::NotOwner { ref range }) if range == "main"),
        "commits stop while the leader is unreachable and none is confirmed: {stopped:?}"
    );
    let promoted = lane_topology_with_owner("m-b1", 2);
    for name in ["m-b1", "m-b2", "m-g1", "m-g2"] {
        h.apply_view(name, promoted.clone());
    }
    faults.push(FaultClass::LeaderLoss);
    faults.push(FaultClass::RegionPartition);
    h.write("m-b1", tenant, "op-b", "doc-shared", "beta-isolated-value");
    h.write("m-a1", tenant, "op-3", "doc-shared", "alpha-isolated-value");

    // The isolated writer acknowledges inside its own Region: r-alpha still
    // has both Members and their link is healthy.
    let acked_op3 =
        match h.wait_ack("m-a1", tenant, "op-3", AcknowledgementLevel::RegionQuorum, ack_ticks) {
            ReplicationOutcome::Acknowledged(report) => {
                (OperationId::try_new("op-3").expect("valid id"), report)
            }
            other => panic!("the isolated Region still reaches its own quorum: {other:?}"),
        };
    h.heal();
    let healed_at = h.transport.now();

    // Act F: the entire r-alpha Region is lost after the acknowledged
    // write; the acknowledged operation is absent from every survivor.
    let fault_at = h.transport.now();
    h.close("m-a1");
    h.close("m-a2");
    faults.push(FaultClass::RegionLossAfterAcknowledgedWrite);
    let survivors = ["m-b1", "m-b2", "m-g1", "m-g2"];
    let acked = [
        AckedOperation {
            operation: acked_op1.0.clone(),
            level: acked_op1.1.observed,
            sequence: 1,
            contributing: acked_op1
                .1
                .contributed
                .iter()
                .flat_map(|(_, members)| members.iter().cloned())
                .collect(),
        },
        AckedOperation {
            operation: acked_op3.0.clone(),
            level: acked_op3.1.observed,
            sequence: 3,
            contributing: acked_op3
                .1
                .contributed
                .iter()
                .flat_map(|(_, members)| members.iter().cloned())
                .collect(),
        },
    ];
    let (rpo_count, rpo_window) = h.rpo(tenant, &acked, &survivors);
    assert_eq!(rpo_count, 1, "the quorum-acknowledged write is lost with its Region");

    // Act G: recovery — two lane ticks of detection and view application,
    // then the first post-fault successful operation by the surviving
    // owner.
    h.transport.pump();
    h.transport.pump();
    h.write("m-b1", tenant, "op-4", "doc-4", "post-fault-value");
    let recovered_at = h.transport.now();
    let rto_ticks = recovered_at.ticks() - fault_at.ticks();

    // Act H: the lag observation for the surviving source, then repair and
    // the convergence observation naming every surviving Member.
    let survivor_lag: Vec<(MemberId, u64)> =
        survivors[1..].iter().map(|name| (h.member_id(name), h.lag("m-b1", name))).collect();
    for target in &survivors[1..] {
        let report = h.replicate("m-b1", target, tenant, 8);
        assert!(report.caught_up);
    }
    let convergence = h.observe("op-4", &survivors, tenant);
    let convergence_ticks = convergence.observed_at.ticks() - healed_at.ticks();
    assert_eq!(
        convergence.holdings.len(),
        survivors.len(),
        "the observation names every surviving Member"
    );

    let bytes = h.bytes_on_wire();
    let receipt = LaneReceipt::new("three-region-composite", lane, &h.topology())
        .with_faults(&faults)
        .with_acknowledgement_report(&acked_op1.0, &acked_op1.1)
        .with_acknowledgement_report(&acked_op3.0, &acked_op3.1)
        .with_acknowledgement_members(
            &OperationId::try_new("op-4").expect("valid id"),
            AcknowledgementLevel::Local,
            &[lane_member_id("m-b1")],
        )
        .with_convergence(convergence, "every surviving Member durably holds the operation")
        .with_rpo(rpo_count, rpo_window)
        .with_rto_ticks(rto_ticks)
        .with_convergence_ticks(convergence_ticks)
        .with_lag(lag_distances.iter().chain(survivor_lag.iter()).cloned().collect())
        .with_bytes_on_wire(bytes)
        .with_limit("lane-scoped facts only: no production topology, timing, or performance claim")
        .with_limit("RPO counts operations acknowledged then absent from every survivor")
        .with_limit(
            "RTO availability condition: first post-fault local write by the surviving owner",
        )
        .with_limit("convergence condition names the surviving Members, not the lost Region")
        .with_limit("replication lag is a journal-sequence distance, not a latency");
    let receipt = match lane {
        LaneKind::InProcess => receipt,
        LaneKind::EngineSocket => receipt.with_limit(
            "the socket lane frames each envelope with a four-byte length prefix on the wire; \
the counters measure envelope bytes at the delivery boundary",
        ),
    };
    let _ = recovered_at;
    h.clean_up();
    receipt
}
