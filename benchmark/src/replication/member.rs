//! The crate-private replication member driver.
//!
//! One [`ReplicationMember`] owns one durable [`Engine`], applies one
//! validated [`ReplicationConfig`], and moves committed operations between
//! members through the deterministic transport doubles. R-1 built the data
//! plane — catch-up from the publication journal, idempotent replay through
//! the W3 operation ledger, a durable [`ReplicationCursor`] derived from the
//! member's own journal, rejoin validation, anti-entropy repair, and tenant
//! isolation at every seam. R-2 adds the adjudicated decisions on top:
//!
//! - D-2: the acknowledgement wait as a separate, explicitly driven step
//!   after the local commit, with contributing Members counted only at
//!   [`LocalDurability::Synced`], degenerate levels normalized and recorded,
//!   and timeouts reported with the partial acknowledgement that names who
//!   had durably recorded.
//! - D-3: a bounded hybrid logical clock per member, deterministic
//!   last-writer-wins convergence, and losers retained as content-free
//!   [`ConflictRecord`] history.
//! - D-1: admission fencing through topology ownership (R-1), apply-time
//!   epoch checks on pushed batches, and deposed-owner writes retained as
//!   conflict history that keep their local receipts.
//!
//! The catch-up protocol stays at-least-once: duplicates are resolved by
//! operation identity, never by de-duplicating delivery.

use std::collections::{BTreeMap, BTreeSet};

use super::identifier::validate_topology_identifier;
use super::status::member_health;
use super::topology::MAX_CATCH_UP_BATCH;
use super::transport::{FrameSelector, ReplicationFrame, ReplicationNetwork, StampedCommit};
use super::{
    AcknowledgementLevel, AcknowledgementReport, ConflictOutcome, ConflictRecord, Convergence,
    MemberId, RegionId, ReplicationClockStamp, ReplicationConfig, ReplicationCursor,
    ReplicationInstant, ReplicationOutcome, ReplicationStatus, TopologyView,
};
use crate::engine::{
    CommitError, CommitOutcome, DurableResolution, Engine, EngineConfig, EngineError,
    EngineOpenError,
};
use crate::storage::{PublishedCommit, PublishedWrite};
use crate::types::{
    Document, DocumentId, ExpectedVersion, LocalDurability, OperationId, StorageSequence, TenantId,
    TenantScope,
};

/// The bounded number of commits one catch-up round requests by default.
pub(crate) const DEFAULT_CATCH_UP_BATCH: u32 = 32;

/// The maximum number of tenant-isolation denials one member retains.
///
/// The log is bounded: a flooding hostile requester can grow it to this bound
/// and no further, and the health observation degrades on the first entry.
#[allow(
    dead_code,
    reason = "driven and observed by the W4-REPLICATION R-1 lane tests; the production acquisition seam lands with R-2/R-3"
)]
pub(crate) const MAX_DENIALS: usize = 64;

/// The maximum number of conflict records one member retains.
///
/// Losers are retained as observable history, and history is bounded the way
/// every other lane observation is: a flooding writer can grow the log to
/// this bound and no further.
pub(crate) const MAX_CONFLICTS: usize = 64;

/// The clock-skew bound B of the D-3 conflict policy, in lane ticks.
///
/// Derivation (a lane-scoped fact, no production claim): the lane clock is
/// the shared transport clock and advances exactly one tick per pump; a
/// member refreshes its clock at every network-touching call and at an
/// explicitly injected instant, so the maximum skew a correct lane member
/// can show against the member observing it is one tick — the tick the
/// peer observed before the observer's last refresh. The bound is four
/// ticks: a stated four-times margin over that one-tick maximum, erring
/// toward classifying genuine reorderings as conflicts rather than
/// trusting a clock that cannot be trusted.
pub(crate) const MAX_CLOCK_SKEW_TICKS: u64 = 4;

/// One replication member: one Engine, one configuration, one identity.
#[allow(
    dead_code,
    reason = "driven and observed by the W4-REPLICATION R-1 lane tests; the production acquisition seam lands with R-2/R-3"
)]
pub(crate) struct ReplicationMember {
    id: MemberId,
    engine: Option<Engine>,
    engine_config: EngineConfig,
    config: ReplicationConfig,
    /// Advisory, in-memory catch-up progress per tenant, expressed in the
    /// serving member's storage-sequence space. It never survives a restart
    /// and is never a durability fact; the journal-derived cursor is.
    advisory: BTreeMap<TenantId, SourceProgress>,
    /// The member's bounded hybrid logical clock.
    hlc: ReplicationClockStamp,
    /// The clock stamp each locally admitted operation was minted at.
    operation_stamps: BTreeMap<OperationId, ReplicationClockStamp>,
    /// The data range each locally admitted operation wrote under.
    operation_ranges: BTreeMap<OperationId, String>,
    /// The last clock stamp each document key carries, and whether that stamp
    /// came from this member's own write. An own stamp is what makes an
    /// incoming write to the same key a concurrent pair.
    key_states: BTreeMap<(TenantId, DocumentId), KeyState>,
    /// The highest ownership epoch seen per data range, from applied views
    /// and received pushes: the apply-time fencing fact of D-1.
    highest_epoch_seen: BTreeMap<String, u64>,
    /// The bounded, observable conflict history.
    conflicts: Vec<ConflictRecord>,
    /// The bounded, observable tenant-isolation denial log.
    denials: Vec<TenantDenial>,
    /// The observable catch-up fallbacks this member performed.
    fallbacks: Vec<CatchUpFallback>,
    /// The observable below-epoch batches this member fenced.
    fenced: Vec<FencedBatch>,
    /// The observable ownership transitions that deposed this member.
    depositions: Vec<Deposition>,
    /// The last rejoin rejection, when rejoin validation refused admission.
    last_rejection: Option<RejoinRejection>,
    activated: bool,
    answered: bool,
}

/// The recorded state of one document key.
#[derive(Clone, Debug, Eq, PartialEq)]
struct KeyState {
    /// The clock stamp of the last write the key carries.
    stamp: ReplicationClockStamp,
    /// The operation that wrote it, when this member wrote it itself.
    own_operation: Option<OperationId>,
}

/// One tenant's advisory progress toward one serving member.
#[derive(Clone, Debug, Eq, PartialEq)]
#[allow(
    dead_code,
    reason = "driven and observed by the W4-REPLICATION R-1 lane tests; the production acquisition seam lands with R-2/R-3"
)]
struct SourceProgress {
    source: MemberId,
    applied_through: Option<StorageSequence>,
    tenant_high_water: Option<StorageSequence>,
}

#[allow(
    dead_code,
    reason = "driven and observed by the W4-REPLICATION R-1 lane tests; the production acquisition seam lands with R-2/R-3"
)]
impl ReplicationMember {
    /// Opens one member over a durable Engine location under a validated
    /// configuration.
    ///
    /// # Errors
    ///
    /// Returns [`MemberError::NotInTopology`] when the view does not seat
    /// this member and [`MemberError::EngineOpen`] when the location cannot
    /// be opened.
    pub(crate) fn try_new(
        config: ReplicationConfig,
        id: MemberId,
        engine_config: EngineConfig,
    ) -> Result<Self, MemberError> {
        if !config.topology().contains_member(&id) {
            return Err(MemberError::NotInTopology);
        }
        let engine = Engine::open(engine_config.clone()).map_err(MemberError::EngineOpen)?;
        let mut member = Self {
            id,
            engine: Some(engine),
            engine_config,
            config,
            advisory: BTreeMap::new(),
            hlc: ReplicationClockStamp::new(0, 0),
            operation_stamps: BTreeMap::new(),
            operation_ranges: BTreeMap::new(),
            key_states: BTreeMap::new(),
            highest_epoch_seen: BTreeMap::new(),
            conflicts: Vec::new(),
            denials: Vec::new(),
            fallbacks: Vec::new(),
            fenced: Vec::new(),
            depositions: Vec::new(),
            last_rejection: None,
            activated: false,
            answered: false,
        };
        member.record_view_epochs();
        Ok(member)
    }

    /// Returns the member identity.
    pub(crate) fn id(&self) -> &MemberId {
        &self.id
    }

    /// Returns the member's Engine, or `None` while it is closed.
    pub(crate) fn engine(&self) -> Option<&Engine> {
        self.engine.as_ref()
    }

    /// Commits one write request locally at the explicit acknowledgement
    /// level it names.
    ///
    /// The local publication completes first and on its own, whatever the
    /// named level is: the acknowledgement wait is a separate, explicitly
    /// driven step (`wait_for_acknowledgement`), exactly like V1's
    /// post-commit waits, and it never blocks or rewrites this path. Under
    /// D-1 the member admits the write only while its current topology view
    /// names it owner of the addressed data range, so a deposed owner's new
    /// writes fail admission locally.
    ///
    /// The write is stamped with the member's bounded hybrid logical clock,
    /// and the stamp is recorded for the range and every document key it
    /// touches — the facts the D-3 conflict policy decides with later.
    ///
    /// # Errors
    ///
    /// Returns [`MemberError::NotOwner`] when this member does not own the
    /// addressed range, and the engine's typed failures otherwise.
    pub(crate) fn commit_write(
        &mut self,
        scope: &TenantScope,
        write: ReplicationWrite,
    ) -> Result<crate::engine::CommitReceipt, MemberError> {
        if self.config.topology().owner_of(&write.range) != Some(&self.id) {
            return Err(MemberError::NotOwner { range: write.range });
        }
        let stamp = self.tick_local_clock();
        let keys = write.keys(scope.tenant());
        let engine = self.engine.as_ref().ok_or(MemberError::EngineClosed)?;
        let receipt = commit_locally(engine, scope, &write)?;
        self.operation_stamps.insert(write.operation.clone(), stamp);
        self.operation_ranges.insert(write.operation.clone(), write.range);
        for key in keys {
            self.key_states
                .insert(key, KeyState { stamp, own_operation: Some(write.operation.clone()) });
        }
        self.activated = true;
        Ok(receipt)
    }

    /// Waits for the requested acknowledgement level of one committed
    /// operation, driving the replication itself round by round.
    ///
    /// This is the D-2 wait, and it is exactly V1's post-commit shape: the
    /// local commit has already happened, the wait never blocks or rewrites
    /// it, and each round pushes the operation to the peers, pumps the lane
    /// clock once, and lets each peer apply. A Member contributes only once
    /// its own publication of the operation reaches
    /// [`LocalDurability::Synced`]. When the level is met the outcome is
    /// [`ReplicationOutcome::Acknowledged`] naming every contributing Region
    /// and Member; when the injected deadline instant arrives first it is
    /// [`ReplicationOutcome::Timeout`] carrying the partial acknowledgement
    /// that names who had durably recorded; when a lost Member makes the
    /// level unreachable it is [`ReplicationOutcome::Failed`] with the typed
    /// category. Degenerate level requests over small topologies are
    /// normalized to the weakest distinct name and the normalization is
    /// recorded in the report.
    ///
    /// # Errors
    ///
    /// Returns nothing: every failure is a [`ReplicationOutcome`] variant,
    /// including the typed [`crate::replication::ReplicationError`] ones.
    #[allow(clippy::too_many_lines)]
    pub(crate) fn wait_for_acknowledgement(
        &mut self,
        network: &mut ReplicationNetwork,
        scope: &TenantScope,
        operation: &OperationId,
        level: AcknowledgementLevel,
        peers: &mut [(&mut ReplicationMember, TenantScope)],
        deadline: ReplicationInstant,
    ) -> ReplicationOutcome {
        use crate::replication::ReplicationError;

        let Some(engine) = self.engine.as_ref() else {
            return ReplicationOutcome::Failed(ReplicationError::EngineClosed);
        };
        // The wait presupposes the local publication at the contributing
        // threshold: a `Synced` local receipt.
        let locally_synced = matches!(
            engine.resolve_commit(scope, operation.clone()),
            Ok(DurableResolution::Committed(receipt))
                if receipt.durability() == LocalDurability::Synced
        );
        if !locally_synced {
            return ReplicationOutcome::Failed(ReplicationError::NotReplicated);
        }
        let Some(range) = self.operation_ranges.get(operation).cloned() else {
            return ReplicationOutcome::Failed(ReplicationError::NotReplicated);
        };
        let epoch = self.config.topology().owner_epoch_of(&range).unwrap_or(1);
        let region_count = self.config.topology().regions().count();
        let (canonical, normalization) = normalize_level(level, region_count);
        let Some(stamped) = self.find_stamped_commit(scope.tenant(), operation) else {
            return ReplicationOutcome::Failed(ReplicationError::NotReplicated);
        };

        loop {
            self.refresh_clock_from(network.now());
            let contributing = self.contributing_members(scope, operation, peers);
            if level_satisfied(canonical, &self.config, &contributing) {
                return ReplicationOutcome::Acknowledged(self.build_report(
                    level,
                    canonical,
                    normalization,
                    &contributing,
                    network.now(),
                ));
            }
            // Reachability: could the level ever be met by the Members that
            // are still alive? If not, the wait fails with the typed
            // category rather than burning the deadline.
            let mut possible = BTreeSet::from([self.id.clone()]);
            possible.extend(
                peers
                    .iter()
                    .filter(|(peer, _)| peer.engine().is_some())
                    .map(|(peer, _)| peer.id().clone()),
            );
            if !level_satisfied(canonical, &self.config, &possible) {
                // The typed category follows the request's own shape: a
                // level naming every Region points at the lost Member
                // itself, a quorum level reports the unreachable quorum.
                let error = if level == AcknowledgementLevel::AllRegions {
                    crate::replication::ReplicationError::MemberUnavailable
                } else {
                    crate::replication::ReplicationError::QuorumUnreachable
                };
                return ReplicationOutcome::Failed(error);
            }
            if network.now() >= deadline {
                let strongest = strongest_satisfied_level(&self.config, &contributing);
                let (partial, _) = normalize_level(strongest, region_count);
                return ReplicationOutcome::Timeout {
                    operation_id: operation.clone(),
                    requested: level,
                    observed: self.build_report(
                        strongest,
                        partial,
                        None,
                        &contributing,
                        network.now(),
                    ),
                };
            }
            // One deterministic round: push, pump, apply.
            for (peer, _) in peers.iter_mut() {
                if peer.engine().is_none() {
                    continue;
                }
                let frame = ReplicationFrame::Push {
                    source: self.id.clone(),
                    range: range.clone(),
                    epoch,
                    tenant: scope.tenant().clone(),
                    commits: vec![stamped.clone()],
                };
                if network.send(&self.id, peer.id(), frame).is_err() {
                    // A bounded link refusing a re-push is a lost round, not
                    // a failed wait: the next round retries at-least-once.
                }
            }
            network.pump();
            for (peer, peer_scope) in peers.iter_mut() {
                if peer.engine().is_none() {
                    continue;
                }
                let _ = peer.apply_deliveries(network, peer_scope);
            }
        }
    }

    /// Returns the Members whose own publication of the operation is
    /// committed at `Synced`: this member, when its receipt says so, plus
    /// every peer that resolves the operation durably at that level.
    fn contributing_members(
        &self,
        scope: &TenantScope,
        operation: &OperationId,
        peers: &[(&mut ReplicationMember, TenantScope)],
    ) -> BTreeSet<MemberId> {
        let mut contributing = BTreeSet::new();
        let own_synced = self.engine.as_ref().is_some_and(|engine| {
            matches!(
                engine.resolve_commit(scope, operation.clone()),
                Ok(DurableResolution::Committed(receipt))
                    if receipt.durability() == LocalDurability::Synced
            )
        });
        if own_synced {
            contributing.insert(self.id.clone());
        }
        for (peer, peer_scope) in peers {
            let holds = peer.engine().is_some_and(|engine| {
                matches!(
                    engine.resolve_commit(peer_scope, operation.clone()),
                    Ok(DurableResolution::Committed(receipt))
                        if receipt.durability() == LocalDurability::Synced
                )
            });
            if holds {
                contributing.insert(peer.id().clone());
            }
        }
        contributing
    }

    /// Builds one acknowledgement report from the contributing set.
    fn build_report(
        &self,
        requested: AcknowledgementLevel,
        observed: AcknowledgementLevel,
        normalization: Option<(AcknowledgementLevel, AcknowledgementLevel)>,
        contributing: &BTreeSet<MemberId>,
        at: ReplicationInstant,
    ) -> AcknowledgementReport {
        let mut grouped: BTreeMap<RegionId, Vec<MemberId>> = BTreeMap::new();
        for member in contributing {
            if let Some(region) = self.config.topology().region_of(member) {
                grouped.entry(region.clone()).or_default().push(member.clone());
            }
        }
        AcknowledgementReport::new(
            requested,
            observed,
            grouped.into_iter().collect(),
            at,
            normalization,
        )
    }

    /// Finds one committed operation in this member's journal with its
    /// stamp.
    fn find_stamped_commit(
        &self,
        tenant: &TenantId,
        operation: &OperationId,
    ) -> Option<StampedCommit> {
        let engine = self.engine.as_ref()?;
        let commits = engine.tenant_commits(tenant, StorageSequence::new(0), MAX_CATCH_UP_BATCH);
        let commit = commits.ok()?.into_iter().find(|commit| commit.operation_id() == operation)?;
        let stamp = self.operation_stamps.get(operation).copied().unwrap_or(self.hlc);
        Some(StampedCommit::new(stamp, commit))
    }

    /// Observes the loss of entire Regions after an acknowledged write.
    ///
    /// The observation states durable visibility — which Regions and Members
    /// durably hold the operation at the loss instant — and the recovery
    /// limit: the lost Regions' state is unknown until one of their Members
    /// rejoins and validates its cursor. Nothing is guessed.
    pub(crate) fn observe_region_loss(
        &self,
        at: ReplicationInstant,
        scope: &TenantScope,
        operation: &OperationId,
        acknowledged: &AcknowledgementReport,
        peers: &[(&ReplicationMember, &TenantScope)],
        lost: &[RegionId],
    ) -> RegionLossObservation {
        let mut holders: BTreeMap<RegionId, Vec<MemberId>> = BTreeMap::new();
        let holds = |member: &ReplicationMember, member_scope: &TenantScope| {
            member.engine().is_some_and(|engine| {
                matches!(
                    engine.resolve_commit(member_scope, operation.clone()),
                    Ok(DurableResolution::Committed(_))
                )
            })
        };
        if holds(self, scope)
            && let Some(region) = self.config.topology().region_of(&self.id)
        {
            holders.entry(region.clone()).or_default().push(self.id.clone());
        }
        for (peer, peer_scope) in peers {
            if holds(peer, peer_scope)
                && let Some(region) = self.config.topology().region_of(peer.id())
            {
                holders.entry(region.clone()).or_default().push(peer.id().clone());
            }
        }
        RegionLossObservation {
            operation: operation.clone(),
            acknowledged: acknowledged.observed(),
            durably_held_by: holders.into_iter().collect(),
            unknown_in: lost.to_vec(),
            observed_at: at,
            limit: RegionLossLimit::UnknownUntilRejoin,
        }
    }

    /// Requests one tenant's catch-up from `source` through the transport.
    ///
    /// The request carries the member's advisory progress in the source's
    /// sequence space when it has one, and asks for a full scan otherwise —
    /// for example after a restart, where resumption re-receives the tenant
    /// history and resolves already-applied operations through the durable
    /// operation ledger.
    ///
    /// # Errors
    ///
    /// Returns [`MemberError::EngineClosed`], [`MemberError::UnknownSource`]
    /// when the source is not seated by this member's view, and
    /// [`MemberError::TransportFull`] when the bounded link rejects the
    /// frame.
    pub(crate) fn request_catch_up(
        &mut self,
        network: &mut ReplicationNetwork,
        source: &MemberId,
        scope: &TenantScope,
    ) -> Result<(), MemberError> {
        if self.engine.is_none() {
            return Err(MemberError::EngineClosed);
        }
        if !self.config.topology().contains_member(source) {
            return Err(MemberError::UnknownSource(source.clone()));
        }
        let after = self
            .advisory
            .get(scope.tenant())
            .filter(|progress| progress.source == *source)
            .and_then(|progress| progress.applied_through);
        network
            .send(
                &self.id,
                source,
                ReplicationFrame::CatchUpRequest {
                    requester: self.id.clone(),
                    tenant: scope.tenant().clone(),
                    after,
                    limit: self.config.catch_up_batch(),
                },
            )
            .map_err(|_| MemberError::TransportFull)
    }

    /// Serves the catch-up requests addressed to this member for the scope's
    /// tenant, returning the number of requests answered.
    ///
    /// A request naming any other tenant is denied with a typed error,
    /// recorded in the observable denial log, and answered with nothing: no
    /// cross-tenant data crosses the seam.
    ///
    /// # Errors
    ///
    /// Returns [`MemberError::CrossTenant`] when a request named another
    /// tenant, [`MemberError::EngineClosed`], and the engine's typed
    /// failures otherwise.
    pub(crate) fn serve_catch_up(
        &mut self,
        network: &mut ReplicationNetwork,
        scope: &TenantScope,
    ) -> Result<usize, MemberError> {
        let tenant = scope.tenant().clone();
        self.refresh_clock_from(network.now());
        let engine = self.engine.as_ref().ok_or(MemberError::EngineClosed)?;
        let requests = network.receive(&self.id, FrameSelector::Requests);
        let mut admitted = Vec::new();
        let mut denials = Vec::new();
        let mut denial_error = None;
        for frame in requests {
            let ReplicationFrame::CatchUpRequest { requester, tenant: requested, after, limit } =
                frame
            else {
                continue;
            };
            if requested == tenant {
                admitted.push((requester, after, limit));
                continue;
            }
            denials.push(TenantDenial::new(
                DenialSeam::ServeRequest,
                tenant.clone(),
                requested.clone(),
                requester,
            ));
            if denial_error.is_none() {
                denial_error =
                    Some(MemberError::CrossTenant { scope: tenant.clone(), attempted: requested });
            }
        }
        let mut served = 0;
        for (requester, after, limit) in admitted {
            let batch = build_catch_up_batch(
                engine,
                &self.id,
                &tenant,
                after,
                limit,
                &self.operation_stamps,
                self.hlc,
            )?;
            network.send(&self.id, &requester, batch).map_err(|_| MemberError::TransportFull)?;
            served += 1;
        }
        for denial in denials {
            self.record_denial(denial);
        }
        if served > 0 {
            self.activated = true;
            self.answered = true;
        }
        match denial_error {
            Some(error) => Err(error),
            None => Ok(served),
        }
    }

    /// Applies the catch-up batches and pushed commits addressed to this
    /// member for the scope's tenant.
    ///
    /// Every delivery is merged and applied in ascending source sequence
    /// order, so reordered deliveries replay to the contract outcome.
    /// Duplicate operations resolve through the durable operation ledger; an
    /// operation identity that arrives bound to a different write set
    /// surfaces as the typed [`CommitError::OperationIdReuse`] rejection and
    /// is never silently skipped. A pushed batch stamped below the highest
    /// epoch this member has seen for its range is fenced — rejected with a
    /// typed error while its commits are retained as observable conflict
    /// history, never silently dropped. A batch the source flagged as below
    /// its servable history either falls back to a full catch-up (when the
    /// flag names this member's own cursor) or fails with the typed
    /// [`MemberError::GapUnservable`].
    ///
    /// A delivered write that competes with one of this member's own writes
    /// on the same document key converges under the D-3 policy:
    /// last-writer-wins on the bounded hybrid logical clock stamps, with the
    /// loser retained as a content-free [`ConflictRecord`].
    ///
    /// # Errors
    ///
    /// Returns [`MemberError::CrossTenant`] when a delivery named another
    /// tenant, [`MemberError::FencedBatch`] for a below-epoch push,
    /// [`MemberError::GapUnservable`] for an unservable cursor,
    /// [`MemberError::ApplyFailed`] for a rejected or undecided apply, and
    /// the engine's typed failures otherwise.
    #[allow(
        clippy::too_many_lines,
        reason = "one delivery pass: admission, fencing, gaps, merge, and apply"
    )]
    pub(crate) fn apply_deliveries(
        &mut self,
        network: &mut ReplicationNetwork,
        scope: &TenantScope,
    ) -> Result<ApplyReport, MemberError> {
        let tenant = scope.tenant().clone();
        self.refresh_clock_from(network.now());
        let frames = network.receive(&self.id, FrameSelector::Deliveries);
        let mut batches = Vec::new();
        for frame in frames {
            match frame {
                ReplicationFrame::CatchUpBatch {
                    source,
                    tenant: batch_tenant,
                    commits,
                    through,
                    tenant_high_water,
                    servable_from,
                    gap_below,
                } => {
                    if batch_tenant != tenant {
                        self.record_denial(TenantDenial::new(
                            DenialSeam::ApplyDelivery,
                            tenant.clone(),
                            batch_tenant.clone(),
                            source.clone(),
                        ));
                        return Err(MemberError::CrossTenant {
                            scope: tenant,
                            attempted: batch_tenant,
                        });
                    }
                    if let Some(gap_cursor) = gap_below {
                        let advisory_cursor = self
                            .advisory
                            .get(&tenant)
                            .and_then(|progress| progress.applied_through);
                        if advisory_cursor == Some(gap_cursor) && gap_cursor.get() > 0 {
                            // The source cannot serve below this member's own
                            // cursor: fall back to a full catch-up, never
                            // silently skip the gap.
                            self.advisory.remove(&tenant);
                            self.record_fallback(CatchUpFallback::new(tenant.clone(), gap_cursor));
                            continue;
                        }
                        return Err(MemberError::GapUnservable {
                            tenant: tenant.clone(),
                            cursor: gap_cursor,
                            servable_from: servable_from.unwrap_or(gap_cursor),
                        });
                    }
                    batches.push(CatchUpDelivery { source, commits, through, tenant_high_water });
                }
                ReplicationFrame::Push { source, range, epoch, tenant: push_tenant, commits } => {
                    if push_tenant != tenant {
                        self.record_denial(TenantDenial::new(
                            DenialSeam::ApplyDelivery,
                            tenant.clone(),
                            push_tenant.clone(),
                            source.clone(),
                        ));
                        return Err(MemberError::CrossTenant {
                            scope: tenant,
                            attempted: push_tenant,
                        });
                    }
                    // Apply-time fencing (D-1): a batch from a deposed owner
                    // is rejected, and its commits are retained as conflict
                    // history so a local durable fact is never silently
                    // discarded.
                    if let Some(highest) = self.highest_epoch_seen.get(&range).copied()
                        && epoch < highest
                    {
                        for stamped in &commits {
                            self.record_conflict(ConflictRecord::new(
                                vec![stamped.commit.operation_id().clone()],
                                vec![source.clone()],
                                vec![stamped.stamp],
                                ConflictOutcome::DeposedOwnerRetained(source.clone()),
                            ));
                        }
                        self.record_fenced(FencedBatch::new(range.clone(), epoch, highest, source));
                        return Err(MemberError::FencedBatch {
                            range,
                            batch_epoch: epoch,
                            highest_epoch: highest,
                        });
                    }
                    self.record_epoch(&range, epoch);
                    let through = commits.last().map(|stamped| stamped.commit.sequence());
                    batches.push(CatchUpDelivery {
                        source,
                        commits,
                        through,
                        tenant_high_water: through,
                    });
                }
                ReplicationFrame::CatchUpRequest { .. } => {}
            }
        }

        let mut report = ApplyReport { batches: batches.len(), ..ApplyReport::default() };
        let resumed_from = self.advisory.get(&tenant).and_then(|progress| progress.applied_through);
        let mut merged: BTreeMap<u64, (&StampedCommit, &MemberId)> = BTreeMap::new();
        for batch in &batches {
            for stamped in &batch.commits {
                if merged
                    .insert(stamped.commit.sequence().get(), (stamped, &batch.source))
                    .is_some()
                {
                    report.duplicates_resolved += 1;
                }
            }
            report.tenant_high_water =
                max_sequence(report.tenant_high_water, batch.tenant_high_water);
        }
        for (sequence, (stamped, source)) in merged {
            let sequence = StorageSequence::new(sequence);
            if let Some(resumed) = resumed_from
                && sequence <= resumed
            {
                report.duplicates_resolved += 1;
                continue;
            }
            match self.resolve_incoming(source, stamped, &tenant) {
                Incoming::ConflictSkipped => {
                    report.conflicts_recorded += 1;
                }
                Incoming::Apply => match self.apply_one(scope, stamped)? {
                    ApplyStep::Applied => report.applied += 1,
                    ApplyStep::Duplicate => report.duplicates_resolved += 1,
                },
            }
            let progress = self.advisory.entry(tenant.clone()).or_insert(SourceProgress {
                source: batches
                    .last()
                    .map_or_else(|| self.id.clone(), |batch| batch.source.clone()),
                applied_through: None,
                tenant_high_water: None,
            });
            progress.applied_through = max_sequence(progress.applied_through, Some(sequence));
        }
        for batch in &batches {
            if let Some(progress) = self.advisory.get_mut(&tenant) {
                progress.source = batch.source.clone();
                progress.tenant_high_water =
                    max_sequence(progress.tenant_high_water, batch.tenant_high_water);
            }
        }
        if let Some(progress) = self.advisory.get(&tenant) {
            report.through = progress.applied_through;
            report.caught_up =
                report.batches > 0 && progress.applied_through >= progress.tenant_high_water;
        } else {
            report.caught_up = report.batches > 0 && report.tenant_high_water.is_none();
        }
        if report.applied > 0 {
            self.activated = true;
        }
        Ok(report)
    }

    /// Decides the D-3 disposition of one incoming commit against this
    /// member's own writes and clock, recording the conflict either way.
    ///
    /// The comparison is a total order on `(stamp, Member)`, so it decides
    /// identically on both sides of a pair and convergence is symmetric.
    /// Because an operation publishes atomically, one disposition covers all
    /// of its keys: the operation applies only when its write beats every
    /// competing own-write of this member.
    fn resolve_incoming(
        &mut self,
        source: &MemberId,
        stamped: &StampedCommit,
        tenant: &TenantId,
    ) -> Incoming {
        let stamp = stamped.stamp;
        // The skew bound B: a peer stamp beyond the local clock plus B is
        // untrusted, so the pair converges as a conflict decided by the
        // deterministic tie-break — never rejected, never clamped.
        if stamp.ticks() > self.hlc.ticks().saturating_add(MAX_CLOCK_SKEW_TICKS) {
            let winner = if (stamp, source.clone()) > (self.hlc, self.id.clone()) {
                source.clone()
            } else {
                self.id.clone()
            };
            self.record_conflict(ConflictRecord::new(
                vec![stamped.commit.operation_id().clone()],
                sorted_members(&[source.clone(), self.id.clone()]),
                vec![stamp, self.hlc],
                ConflictOutcome::UntrustedClock(winner.clone()),
            ));
            return if winner == self.id { Incoming::ConflictSkipped } else { Incoming::Apply };
        }
        // A trusted peer stamp advances the local hybrid clock.
        self.hlc = merge_stamp(self.hlc, stamp);
        let keys = written_keys(tenant, &stamped.commit);
        let competing: Vec<(&(TenantId, DocumentId), &KeyState)> = self
            .key_states
            .iter()
            .filter(|(key, state)| keys.contains(key) && state.own_operation.is_some())
            .collect();
        if competing.is_empty() {
            return Incoming::Apply;
        }
        let incoming: (&ReplicationClockStamp, &MemberId) = (&stamp, source);
        let beats_every = competing.iter().all(|(_, state)| incoming > (&state.stamp, &self.id));
        let mut operations: Vec<OperationId> =
            competing.iter().filter_map(|(_, state)| state.own_operation.clone()).collect();
        let mut clocks: Vec<ReplicationClockStamp> =
            competing.iter().map(|(_, state)| state.stamp).collect();
        operations.push(stamped.commit.operation_id().clone());
        clocks.push(stamp);
        let (operations, clocks) = sort_record_facts(operations, clocks);
        if beats_every {
            // The incoming write wins every competing key: it applies, and
            // this member's own losing writes are retained as history.
            self.record_conflict(ConflictRecord::new(
                operations,
                sorted_members(&[source.clone(), self.id.clone()]),
                clocks,
                ConflictOutcome::Winner(source.clone()),
            ));
            Incoming::Apply
        } else {
            // At least one of this member's own writes wins (or the
            // deterministic tie favors it), so the atomic operation is
            // skipped and the incoming write is retained as history.
            self.record_conflict(ConflictRecord::new(
                operations,
                sorted_members(&[source.clone(), self.id.clone()]),
                clocks,
                ConflictOutcome::Winner(self.id.clone()),
            ));
            Incoming::ConflictSkipped
        }
    }

    /// Applies one delivered commit through the Engine transaction seam with
    /// its original operation identity, recording its key state.
    ///
    /// The commit is always staged and committed, never short-circuited by a
    /// pre-check: the engine's operation ledger is the component that binds
    /// an operation identity to its write set, so it is the one that decides
    /// between an idempotent replay (same write set) and the typed
    /// [`CommitError::OperationIdReuse`] rejection (a different write set
    /// under a live identity). The pre-resolve only classifies the outcome
    /// for the caller's accounting.
    fn apply_one(
        &mut self,
        scope: &TenantScope,
        stamped: &StampedCommit,
    ) -> Result<ApplyStep, MemberError> {
        let commit = &stamped.commit;
        let engine = self.engine.as_ref().ok_or(MemberError::EngineClosed)?;
        let already_committed = matches!(
            engine
                .resolve_commit(scope, commit.operation_id().clone())
                .map_err(MemberError::from)?,
            DurableResolution::Committed(_)
        );
        let snapshot = engine.read_snapshot(scope).map_err(MemberError::from)?;
        let mut transaction = engine
            .begin(scope, snapshot, commit.operation_id().clone())
            .map_err(MemberError::from)?;
        for write in commit.writes() {
            match write {
                PublishedWrite::Put { document } => {
                    transaction
                        .put(document.clone(), ExpectedVersion::Any)
                        .map_err(MemberError::from)?;
                }
                PublishedWrite::Delete { collection, id, version_at_delete } => {
                    // The recorded key and version bind the replay: an
                    // identifier that currently lives in a different
                    // collection than the source deleted from is a typed
                    // mismatch, never a silent removal of another key, and a
                    // recorded version of zero is the provable no-op delete.
                    if let Some(recorded) = collection
                        && let Some(existing) = transaction.read(id)?
                        && existing.collection() != recorded
                    {
                        return Err(MemberError::ApplyFailed {
                            operation: commit.operation_id().clone(),
                            reason: ApplyFailure::KeyMismatch,
                        });
                    }
                    let expected = if *version_at_delete == 0 {
                        ExpectedVersion::Any
                    } else {
                        ExpectedVersion::Exact(*version_at_delete)
                    };
                    transaction.delete(id.clone(), expected).map_err(MemberError::from)?;
                }
            }
        }
        match transaction.commit(LocalDurability::Synced) {
            CommitOutcome::Committed(_) => {
                // The applied write now carries the incoming stamp on every
                // key it touched; it is this member's state, not its own
                // write, so a later competing delivery is compared against
                // it without being a writer conflict.
                for key in written_keys(scope.tenant(), commit) {
                    self.key_states
                        .insert(key, KeyState { stamp: stamped.stamp, own_operation: None });
                }
                Ok(if already_committed { ApplyStep::Duplicate } else { ApplyStep::Applied })
            }
            CommitOutcome::Rejected(reason) => Err(MemberError::ApplyFailed {
                operation: commit.operation_id().clone(),
                reason: ApplyFailure::Rejected(reason),
            }),
            CommitOutcome::Unknown(unknown) => {
                match engine
                    .resolve_commit(scope, unknown.operation_id().clone())
                    .map_err(MemberError::from)?
                {
                    DurableResolution::Committed(_) => {
                        for key in written_keys(scope.tenant(), commit) {
                            self.key_states.insert(
                                key,
                                KeyState { stamp: stamped.stamp, own_operation: None },
                            );
                        }
                        Ok(ApplyStep::Applied)
                    }
                    DurableResolution::Rejected(reason) => Err(MemberError::ApplyFailed {
                        operation: commit.operation_id().clone(),
                        reason: ApplyFailure::Rejected(reason),
                    }),
                    DurableResolution::Pending => Err(MemberError::ApplyFailed {
                        operation: commit.operation_id().clone(),
                        reason: ApplyFailure::Pending,
                    }),
                }
            }
        }
    }

    /// Applies a new membership view to this member (a control-plane
    /// transition the member validates before admitting).
    ///
    /// The view must seat this member and must not lower the epoch of any
    /// range this member holds. Losing ownership of a range records the
    /// deposition: from that moment the member's own writes to the range
    /// fail admission (D-1), and its already-committed writes keep their
    /// local receipts and converge under D-3.
    ///
    /// # Errors
    ///
    /// Returns [`MemberError::InvalidTopology`] for an unservable view,
    /// [`MemberError::NotInTopology`] when the view does not seat this
    /// member, and [`MemberError::StaleView`] when a held range's epoch would
    /// go backwards or flip owner without a higher epoch.
    pub(crate) fn apply_view(&mut self, view: TopologyView) -> Result<(), MemberError> {
        let view = view.validated().map_err(|_| MemberError::InvalidTopology)?;
        if !view.contains_member(&self.id) {
            return Err(MemberError::NotInTopology);
        }
        for range in self.config.topology().owned_ranges().collect::<Vec<_>>() {
            let current_owner_is_me = self.config.topology().owner_of(range) == Some(&self.id);
            if !current_owner_is_me {
                continue;
            }
            let current_epoch = self.config.topology().owner_epoch_of(range).unwrap_or(1);
            let Some(new_owner) = view.owner_of(range) else {
                continue;
            };
            let offered_epoch = view.owner_epoch_of(range).unwrap_or(1);
            let acceptable = if new_owner == &self.id {
                offered_epoch >= current_epoch
            } else {
                offered_epoch > current_epoch
            };
            if !acceptable {
                return Err(MemberError::StaleView {
                    range: range.to_owned(),
                    current_epoch,
                    offered_epoch,
                });
            }
            if new_owner != &self.id {
                self.depositions.push(Deposition::new(
                    range.to_owned(),
                    current_epoch,
                    offered_epoch,
                    new_owner.clone(),
                ));
            }
        }
        let acknowledgement = self.config.acknowledgement();
        let batch = self.config.catch_up_batch();
        let mut config =
            ReplicationConfig::try_new(view).map_err(|_| MemberError::InvalidTopology)?;
        config = config.with_acknowledgement(acknowledgement);
        config = config.with_catch_up_batch(batch).map_err(|_| MemberError::InvalidTopology)?;
        self.config = config;
        self.record_view_epochs();
        Ok(())
    }

    /// Records the epochs of every range the current view assigns.
    fn record_view_epochs(&mut self) {
        let stamps: Vec<(String, u64)> = self
            .config
            .topology()
            .owned_ranges()
            .filter_map(|range| {
                self.config.topology().owner_epoch_of(range).map(|epoch| (range.to_owned(), epoch))
            })
            .collect();
        for (range, epoch) in stamps {
            self.record_epoch(&range, epoch);
        }
    }

    /// Raises the highest epoch seen for one range.
    fn record_epoch(&mut self, range: &str, epoch: u64) {
        let highest = self.highest_epoch_seen.entry(range.to_owned()).or_insert(0);
        if epoch > *highest {
            *highest = epoch;
        }
    }

    /// Observes an injected lane instant into the member's clock.
    ///
    /// This is how a test moves a member's hybrid clock forward without any
    /// network traffic: the physical component takes the instant when it is
    /// ahead, and the logical counter is untouched. Deterministic by
    /// construction — nothing reads wall time.
    pub(crate) fn observe_instant(&mut self, at: ReplicationInstant) {
        self.refresh_clock_from(at);
    }

    /// Refreshes the clock's physical component from a lane instant.
    fn refresh_clock_from(&mut self, at: ReplicationInstant) {
        if at.ticks() > self.hlc.ticks() {
            self.hlc = ReplicationClockStamp::new(at.ticks(), self.hlc.counter());
        }
    }

    /// Mints the next local stamp: the logical counter ticks within the
    /// current physical tick.
    fn tick_local_clock(&mut self) -> ReplicationClockStamp {
        self.hlc =
            ReplicationClockStamp::new(self.hlc.ticks(), self.hlc.counter().saturating_add(1));
        self.hlc
    }

    /// Records one conflict, deduplicated against the retained history and
    /// bounded by [`MAX_CONFLICTS`].
    fn record_conflict(&mut self, record: ConflictRecord) {
        if self.conflicts.iter().any(|existing| {
            existing.operations() == record.operations() && existing.outcome() == record.outcome()
        }) {
            return;
        }
        if self.conflicts.len() >= MAX_CONFLICTS {
            self.conflicts.remove(0);
        }
        self.conflicts.push(record);
    }

    /// Records one fenced batch.
    fn record_fenced(&mut self, batch: FencedBatch) {
        self.fenced.push(batch);
    }

    /// Returns the bounded, observable conflict history of this member.
    pub(crate) fn conflicts(&self) -> &[ConflictRecord] {
        &self.conflicts
    }

    /// Returns the observable below-epoch batches this member fenced.
    pub(crate) fn fenced(&self) -> &[FencedBatch] {
        &self.fenced
    }

    /// Returns the observable ownership transitions that deposed this
    /// member.
    pub(crate) fn depositions(&self) -> &[Deposition] {
        &self.depositions
    }

    /// Closes the member's Engine; the durable location keeps its bytes.
    pub(crate) fn close(&mut self) {
        if let Some(engine) = self.engine.take() {
            let _ = engine.shutdown();
        }
        self.reset_memory_state();
    }

    /// Reopens the member's Engine from its durable location.
    ///
    /// Advisory progress, clock state, and key states do not survive a
    /// restart — they are in-memory facts — while the durable cursor does,
    /// because it is derived from the journal on the other side. The
    /// implementation record names the cross-restart ordering limit this
    /// implies for the D-3 policy.
    ///
    /// # Errors
    ///
    /// Returns [`MemberError::EngineOpen`] when the location cannot be
    /// reopened and the engine's typed shutdown failure otherwise.
    pub(crate) fn restart(&mut self) -> Result<(), MemberError> {
        if let Some(engine) = self.engine.take() {
            engine.shutdown().map_err(|_| MemberError::Engine(EngineError::StorageFailure))?;
        }
        let engine = Engine::open(self.engine_config.clone()).map_err(MemberError::EngineOpen)?;
        self.engine = Some(engine);
        self.reset_memory_state();
        Ok(())
    }

    /// Clears the in-memory replication state that never survives a member
    /// restart.
    fn reset_memory_state(&mut self) {
        self.advisory.clear();
        self.operation_stamps.clear();
        self.operation_ranges.clear();
        self.key_states.clear();
        self.hlc = ReplicationClockStamp::new(0, 0);
        self.activated = false;
        self.answered = false;
    }

    /// Rejoins the member after validating its identity, format, and cursor.
    ///
    /// The reopen is the identity and format check: a location holding
    /// another storage format, or bytes that do not verify, fails it. The
    /// cursor check compares the freshly journal-derived cursor with the
    /// cursor the member left with: a regression means the durable history
    /// went backwards, and the member is reported and not admitted.
    ///
    /// # Errors
    ///
    /// Returns [`MemberError::RejoinRejected`] when validation refuses
    /// admission, and the reopen's typed failures otherwise.
    pub(crate) fn rejoin(
        &mut self,
        previous: &ReplicationCursor,
    ) -> Result<ReplicationCursor, MemberError> {
        self.restart()?;
        let derived = self.cursor()?;
        if !cursor_progression_is_valid(previous, &derived) {
            let rejection =
                RejoinRejection::new(previous, &derived, RejoinReason::CursorRegression);
            self.last_rejection = Some(rejection.clone());
            self.activated = false;
            return Err(MemberError::RejoinRejected(rejection));
        }
        self.activated = true;
        Ok(derived)
    }

    /// Returns the member's durable replication cursor, derived from its own
    /// publication-history journal.
    ///
    /// # Errors
    ///
    /// Returns [`MemberError::EngineClosed`] and the engine's typed
    /// failures.
    pub(crate) fn cursor(&self) -> Result<ReplicationCursor, MemberError> {
        let engine = self.engine.as_ref().ok_or(MemberError::EngineClosed)?;
        ReplicationCursor::from_journal(&self.id, engine)
    }

    /// Returns the advisory, in-memory progress for one tenant, expressed in
    /// the serving member's sequence space.
    pub(crate) fn advisory_progress(&self, tenant: &TenantId) -> Option<StorageSequence> {
        self.advisory.get(tenant).and_then(|progress| progress.applied_through)
    }

    /// Returns the observable tenant-isolation denial log.
    pub(crate) fn denials(&self) -> &[TenantDenial] {
        &self.denials
    }

    /// Returns the observable catch-up fallbacks this member performed.
    pub(crate) fn fallbacks(&self) -> &[CatchUpFallback] {
        &self.fallbacks
    }

    /// Returns the last rejoin rejection, when validation refused admission.
    pub(crate) fn last_rejection(&self) -> Option<&RejoinRejection> {
        self.last_rejection.as_ref()
    }

    /// Returns the member's health observation, which never carries or
    /// implies a convergence fact.
    pub(crate) fn health(&self) -> ReplicationStatus {
        member_health(self)
    }

    /// Reports whether any tracked tenant is behind its last-known source
    /// high water.
    pub(crate) fn is_lagging(&self) -> bool {
        self.advisory.values().any(|progress| {
            match (progress.applied_through, progress.tenant_high_water) {
                (Some(applied), Some(water)) => applied < water,
                (None, Some(_)) => true,
                (_, None) => false,
            }
        })
    }

    /// Reports whether the member has served or applied anything yet.
    pub(crate) fn activated(&self) -> bool {
        self.activated
    }

    /// Reports whether the member answered a catch-up request.
    pub(crate) fn answered(&self) -> bool {
        self.answered
    }

    /// Records one denial, evicting the oldest beyond the bound.
    fn record_denial(&mut self, denial: TenantDenial) {
        if self.denials.len() >= MAX_DENIALS {
            self.denials.remove(0);
        }
        self.denials.push(denial);
    }

    /// Records one catch-up fallback.
    fn record_fallback(&mut self, fallback: CatchUpFallback) {
        self.fallbacks.push(fallback);
    }
}

/// One write request a member commits locally at a named acknowledgement
/// level.
#[allow(
    dead_code,
    reason = "driven and observed by the W4-REPLICATION R-1 lane tests; the production acquisition seam lands with R-2/R-3"
)]
pub(crate) struct ReplicationWrite {
    operation: OperationId,
    range: String,
    acknowledgement: AcknowledgementLevel,
    writes: Vec<LocalWrite>,
}

/// One staged mutation of a local write request.
#[allow(
    dead_code,
    reason = "driven and observed by the W4-REPLICATION R-1 lane tests; the production acquisition seam lands with R-2/R-3"
)]
enum LocalWrite {
    Put(Document),
    Delete(DocumentId),
}

#[allow(
    dead_code,
    reason = "driven and observed by the W4-REPLICATION R-1 lane tests; the production acquisition seam lands with R-2/R-3"
)]
impl ReplicationWrite {
    /// Creates one write request for a data range at an explicit
    /// acknowledgement level.
    ///
    /// # Errors
    ///
    /// Returns [`MemberError::InvalidRange`] for invalid range text.
    pub(crate) fn new(
        operation: OperationId,
        range: &str,
        acknowledgement: AcknowledgementLevel,
    ) -> Result<Self, MemberError> {
        validate_topology_identifier(range).map_err(|_| MemberError::InvalidRange)?;
        Ok(Self { operation, range: range.to_owned(), acknowledgement, writes: Vec::new() })
    }

    /// Stages one document put.
    pub(crate) fn with_put(mut self, document: Document) -> Self {
        self.writes.push(LocalWrite::Put(document));
        self
    }

    /// Stages one document deletion.
    pub(crate) fn with_delete(mut self, id: DocumentId) -> Self {
        self.writes.push(LocalWrite::Delete(id));
        self
    }

    /// Returns the document keys the write touches, in staging order.
    fn keys(&self, tenant: &TenantId) -> Vec<(TenantId, DocumentId)> {
        self.writes
            .iter()
            .map(|write| match write {
                LocalWrite::Put(document) => document.id().clone(),
                LocalWrite::Delete(id) => id.clone(),
            })
            .map(|id| (tenant.clone(), id))
            .collect()
    }
}

impl ReplicationCursor {
    /// Creates the zero-progress cursor of one member.
    ///
    /// The cursor carries identities and storage sequences only: no document,
    /// payload, digest, path, or transport detail crosses it.
    #[must_use]
    pub fn start(member: MemberId) -> Self {
        Self { member, high_water: None, tenants: Vec::new() }
    }

    /// Returns the member whose durable progress this cursor reports.
    #[must_use]
    pub fn member(&self) -> &MemberId {
        &self.member
    }

    /// Returns the last sequence the member's own journal durably recorded.
    #[must_use]
    pub fn high_water(&self) -> Option<StorageSequence> {
        self.high_water
    }

    /// Returns the member's latest journaled sequence for one tenant.
    #[must_use]
    pub fn tenant_progress(&self, tenant: &TenantId) -> Option<StorageSequence> {
        self.tenants.iter().find(|(recorded, _)| recorded == tenant).map(|(_, sequence)| *sequence)
    }

    /// Returns every tenant this cursor records progress for, in canonical
    /// tenant order.
    pub fn tenants(&self) -> impl Iterator<Item = &TenantId> {
        self.tenants.iter().map(|(tenant, _)| tenant)
    }

    /// Reports whether the cursor records no durable progress.
    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.high_water.is_none() && self.tenants.is_empty()
    }

    /// Derives one member's durable cursor from its own publication-history
    /// journal: the journal is the ground truth of what that member durably
    /// applied, so the cursor is expressed in the member's own storage
    /// sequence space and survives an engine restart unchanged.
    ///
    /// # Errors
    ///
    /// Returns the engine's typed failures.
    #[allow(
        dead_code,
        reason = "driven and observed by the W4-REPLICATION R-1 lane tests; the production acquisition seam lands with R-2/R-3"
    )]
    pub(crate) fn from_journal(member: &MemberId, engine: &Engine) -> Result<Self, MemberError> {
        let high_water = engine.journaled_high_water().map_err(MemberError::from)?;
        let mut tenants = Vec::new();
        for tenant in engine.committed_tenants().map_err(MemberError::from)? {
            let progress =
                engine.tenant_sequence(&tenant, high_water.unwrap_or(StorageSequence::new(0)));
            if let Some(sequence) = progress.map_err(MemberError::from)? {
                tenants.push((tenant, sequence));
            }
        }
        Ok(Self { member: member.clone(), high_water, tenants })
    }
}

/// Commits one write request through the Engine transaction seam.
#[allow(
    dead_code,
    reason = "driven and observed by the W4-REPLICATION R-1 lane tests; the production acquisition seam lands with R-2/R-3"
)]
fn commit_locally(
    engine: &Engine,
    scope: &TenantScope,
    write: &ReplicationWrite,
) -> Result<crate::engine::CommitReceipt, MemberError> {
    let snapshot = engine.read_snapshot(scope).map_err(MemberError::from)?;
    let mut transaction =
        engine.begin(scope, snapshot, write.operation.clone()).map_err(MemberError::from)?;
    for staged in &write.writes {
        match staged {
            LocalWrite::Put(document) => {
                transaction
                    .put(document.clone(), ExpectedVersion::Any)
                    .map_err(MemberError::from)?;
            }
            LocalWrite::Delete(id) => {
                transaction.delete(id.clone(), ExpectedVersion::Any).map_err(MemberError::from)?;
            }
        }
    }
    match transaction.commit(LocalDurability::Synced) {
        CommitOutcome::Committed(receipt) => Ok(receipt),
        CommitOutcome::Rejected(reason) => Err(MemberError::ApplyFailed {
            operation: write.operation.clone(),
            reason: ApplyFailure::Rejected(reason),
        }),
        CommitOutcome::Unknown(unknown) => {
            match engine
                .resolve_commit(scope, unknown.operation_id().clone())
                .map_err(MemberError::from)?
            {
                DurableResolution::Committed(receipt) => Ok(receipt),
                DurableResolution::Rejected(reason) => Err(MemberError::ApplyFailed {
                    operation: write.operation.clone(),
                    reason: ApplyFailure::Rejected(reason),
                }),
                DurableResolution::Pending => Err(MemberError::ApplyFailed {
                    operation: write.operation.clone(),
                    reason: ApplyFailure::Pending,
                }),
            }
        }
    }
}

/// Builds one catch-up batch from the serving member's journal.
#[allow(
    dead_code,
    reason = "driven and observed by the W4-REPLICATION R-1 lane tests; the production acquisition seam lands with R-2/R-3"
)]
fn build_catch_up_batch(
    engine: &Engine,
    source: &MemberId,
    tenant: &TenantId,
    after: Option<StorageSequence>,
    limit: u32,
    operation_stamps: &BTreeMap<OperationId, ReplicationClockStamp>,
    server_clock: ReplicationClockStamp,
) -> Result<ReplicationFrame, MemberError> {
    let servable_from = engine.servable_history_start().map_err(MemberError::from)?;
    let gap_below = catch_up_gap(after, servable_from);
    let (commits, through) = if gap_below.is_some() {
        // Serving below the servable bound would silently skip a gap, so
        // the batch is empty and the gap is named instead.
        (Vec::new(), None)
    } else {
        let served = engine
            .tenant_commits(tenant, after.unwrap_or(StorageSequence::new(0)), limit)
            .map_err(MemberError::from)?;
        let through = served.last().map(PublishedCommit::sequence);
        // History the server no longer holds a stamp for is served at the
        // server's current clock; the implementation record states the
        // cross-restart ordering limit of that rule.
        let commits = served
            .into_iter()
            .map(|commit| {
                let stamp =
                    operation_stamps.get(commit.operation_id()).copied().unwrap_or(server_clock);
                StampedCommit::new(stamp, commit)
            })
            .collect::<Vec<_>>();
        (commits, through)
    };
    let high_water = engine.journaled_high_water().map_err(MemberError::from)?;
    let tenant_high_water = high_water
        .map(|at| engine.tenant_sequence(tenant, at))
        .transpose()
        .map_err(MemberError::from)?
        .flatten();
    Ok(ReplicationFrame::CatchUpBatch {
        source: source.clone(),
        tenant: tenant.clone(),
        commits,
        through,
        tenant_high_water,
        servable_from,
        gap_below,
    })
}

/// Returns the cursor position a source cannot serve below, if any.
///
/// A request is servable incrementally when the journal's oldest servable
/// sequence is at most one past the requester's cursor; anything earlier is
/// a gap that must fall back to a full catch-up, never a silent skip. A
/// requester with no progress needs the journal to serve from its first
/// recorded sequence.
pub(crate) fn catch_up_gap(
    after: Option<StorageSequence>,
    servable_from: Option<StorageSequence>,
) -> Option<StorageSequence> {
    let required_from = after.map_or(0, StorageSequence::get);
    let servable = servable_from.map_or(0, StorageSequence::get);
    if servable > required_from.saturating_add(1) {
        Some(StorageSequence::new(required_from))
    } else {
        None
    }
}

/// One delivered catch-up batch, already admitted for the applying tenant.
#[allow(
    dead_code,
    reason = "driven and observed by the W4-REPLICATION R-1 lane tests; the production acquisition seam lands with R-2/R-3"
)]
struct CatchUpDelivery {
    source: MemberId,
    commits: Vec<StampedCommit>,
    through: Option<StorageSequence>,
    tenant_high_water: Option<StorageSequence>,
}

/// The outcome of applying one commit.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum ApplyStep {
    /// The commit applied through a fresh Engine transaction.
    Applied,
    /// The durable operation ledger had already resolved the operation.
    Duplicate,
}

/// The D-3 disposition of one incoming commit.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
enum Incoming {
    /// The write beats every competing own-write and applies.
    Apply,
    /// A competing own-write won, so the atomic operation is skipped and
    /// the incoming write is retained as conflict history.
    ConflictSkipped,
}

/// The report of one apply pass.
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
#[allow(
    dead_code,
    reason = "driven and observed by the W4-REPLICATION R-1 lane tests; the production acquisition seam lands with R-2/R-3"
)]
pub(crate) struct ApplyReport {
    applied: usize,
    duplicates_resolved: usize,
    conflicts_recorded: usize,
    batches: usize,
    through: Option<StorageSequence>,
    tenant_high_water: Option<StorageSequence>,
    caught_up: bool,
}

#[allow(
    dead_code,
    reason = "driven and observed by the W4-REPLICATION R-1 lane tests; the production acquisition seam lands with R-2/R-3"
)]
impl ApplyReport {
    /// Returns the number of commits this pass applied.
    pub(crate) fn applied(&self) -> usize {
        self.applied
    }

    /// Returns the number of redelivered operations this pass resolved
    /// idempotently through the durable operation ledger.
    pub(crate) fn duplicates_resolved(&self) -> usize {
        self.duplicates_resolved
    }

    /// Returns the number of incoming writes this pass resolved as conflicts
    /// under the D-3 policy, skipping them in favor of the winning write.
    pub(crate) fn conflicts_recorded(&self) -> usize {
        self.conflicts_recorded
    }

    /// Returns this member's advisory progress after the pass.
    pub(crate) fn through(&self) -> Option<StorageSequence> {
        self.through
    }

    /// Reports whether this pass reached the source's last-known high water
    /// for the tenant. A pass that received no batch proves nothing and is
    /// never reported as caught up.
    pub(crate) fn caught_up(&self) -> bool {
        self.caught_up
    }
}

/// One observable tenant-isolation denial.
#[derive(Clone, Debug, Eq, PartialEq)]
#[allow(
    dead_code,
    reason = "driven and observed by the W4-REPLICATION R-1 lane tests; the production acquisition seam lands with R-2/R-3"
)]
pub(crate) struct TenantDenial {
    seam: DenialSeam,
    scope_tenant: TenantId,
    attempted_tenant: TenantId,
    counterparty: MemberId,
}

#[allow(
    dead_code,
    reason = "driven and observed by the W4-REPLICATION R-1 lane tests; the production acquisition seam lands with R-2/R-3"
)]
impl TenantDenial {
    /// Records one denial from its facts.
    fn new(
        seam: DenialSeam,
        scope_tenant: TenantId,
        attempted_tenant: TenantId,
        counterparty: MemberId,
    ) -> Self {
        Self { seam, scope_tenant, attempted_tenant, counterparty }
    }

    /// Returns the seam the denial happened at.
    pub(crate) fn seam(&self) -> DenialSeam {
        self.seam
    }

    /// Returns the tenant the caller's scope named.
    pub(crate) fn scope_tenant(&self) -> &TenantId {
        &self.scope_tenant
    }

    /// Returns the tenant the denied attempt named.
    pub(crate) fn attempted_tenant(&self) -> &TenantId {
        &self.attempted_tenant
    }

    /// Returns the member on the other side of the denied attempt.
    pub(crate) fn counterparty(&self) -> &MemberId {
        &self.counterparty
    }
}

/// The seam a tenant-isolation denial happened at.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum DenialSeam {
    /// A catch-up request named a tenant the serving scope does not.
    ServeRequest,
    /// A catch-up delivery named a tenant the applying scope does not.
    ApplyDelivery,
}

/// One observable catch-up fallback to a full scan.
#[derive(Clone, Debug, Eq, PartialEq)]
#[allow(
    dead_code,
    reason = "driven and observed by the W4-REPLICATION R-1 lane tests; the production acquisition seam lands with R-2/R-3"
)]
pub(crate) struct CatchUpFallback {
    tenant: TenantId,
    cursor: StorageSequence,
}

#[allow(
    dead_code,
    reason = "driven and observed by the W4-REPLICATION R-1 lane tests; the production acquisition seam lands with R-2/R-3"
)]
impl CatchUpFallback {
    /// Records one fallback from its facts.
    fn new(tenant: TenantId, cursor: StorageSequence) -> Self {
        Self { tenant, cursor }
    }

    /// Returns the tenant the fallback re-scans.
    pub(crate) fn tenant(&self) -> &TenantId {
        &self.tenant
    }

    /// Returns the cursor position the source could not serve below.
    pub(crate) fn cursor(&self) -> StorageSequence {
        self.cursor
    }
}

/// One observable rejoin rejection.
#[derive(Clone, Debug, Eq, PartialEq)]
#[allow(
    dead_code,
    reason = "driven and observed by the W4-REPLICATION R-1 lane tests; the production acquisition seam lands with R-2/R-3"
)]
pub(crate) struct RejoinRejection {
    previous: Option<StorageSequence>,
    derived: Option<StorageSequence>,
    reason: RejoinReason,
}

#[allow(
    dead_code,
    reason = "driven and observed by the W4-REPLICATION R-1 lane tests; the production acquisition seam lands with R-2/R-3"
)]
impl RejoinRejection {
    /// Records one rejection from its facts.
    fn new(
        previous: &ReplicationCursor,
        derived: &ReplicationCursor,
        reason: RejoinReason,
    ) -> Self {
        Self { previous: previous.high_water(), derived: derived.high_water(), reason }
    }

    /// Returns the cursor the member rejoined with.
    pub(crate) fn previous(&self) -> Option<StorageSequence> {
        self.previous
    }

    /// Returns the cursor the journal derived after the reopen.
    pub(crate) fn derived(&self) -> Option<StorageSequence> {
        self.derived
    }

    /// Returns the typed rejection reason.
    pub(crate) fn reason(&self) -> RejoinReason {
        self.reason
    }
}

/// Why a rejoin was refused.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum RejoinReason {
    /// The journal-derived cursor regressed below the cursor the member
    /// rejoined with.
    CursorRegression,
}

/// Typed member-driver failures.
#[derive(Clone, Debug, Eq, PartialEq)]
pub(crate) enum MemberError {
    /// The member is not seated by the supplied topology view.
    NotInTopology,
    /// The supplied topology view or configuration input is invalid.
    InvalidTopology,
    /// The addressed source member is not seated by this member's view.
    UnknownSource(MemberId),
    /// The named data range is not valid topology input.
    InvalidRange,
    /// This member does not own the named data range's write path.
    NotOwner {
        /// The range the write addressed.
        range: String,
    },
    /// The supplied view lowers or flips an epoch this member holds without
    /// a higher epoch, so it is a stale transition.
    StaleView {
        /// The range whose ownership would regress.
        range: String,
        /// The epoch this member's current view holds.
        current_epoch: u64,
        /// The epoch the offered view names.
        offered_epoch: u64,
    },
    /// A pushed batch is stamped below the highest epoch this member has
    /// seen for its range: the push comes from a deposed owner (D-1).
    FencedBatch {
        /// The range the pushed batch claimed.
        range: String,
        /// The epoch the batch carried.
        batch_epoch: u64,
        /// The highest epoch this member has seen for the range.
        highest_epoch: u64,
    },
    /// A cross-tenant attempt was denied at the seam.
    CrossTenant {
        /// The tenant the caller's scope named.
        scope: TenantId,
        /// The tenant the denied attempt named.
        attempted: TenantId,
    },
    /// A delivered commit was not applied; the reason is typed.
    ApplyFailed {
        /// The operation identity that failed.
        operation: OperationId,
        /// The typed failure.
        reason: ApplyFailure,
    },
    /// The source cannot serve the requested range of history.
    GapUnservable {
        /// The tenant whose history cannot be served.
        tenant: TenantId,
        /// The cursor position that cannot be served below.
        cursor: StorageSequence,
        /// The oldest sequence the source's journal can serve.
        servable_from: StorageSequence,
    },
    /// The bounded transport link rejected a frame.
    TransportFull,
    /// The member's engine is closed.
    EngineClosed,
    /// Rejoin validation refused admission.
    RejoinRejected(RejoinRejection),
    /// The engine returned a typed failure.
    Engine(EngineError),
    /// The engine could not be opened.
    EngineOpen(EngineOpenError),
}

impl From<EngineError> for MemberError {
    fn from(error: EngineError) -> Self {
        match error {
            EngineError::Closed(_) => Self::EngineClosed,
            other => Self::Engine(other),
        }
    }
}

impl From<EngineOpenError> for MemberError {
    fn from(error: EngineOpenError) -> Self {
        Self::EngineOpen(error)
    }
}

/// Why a delivered commit did not apply.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum ApplyFailure {
    /// The engine rejected the write set; a rejection carrying
    /// [`CommitError::OperationIdReuse`] means the operation identity is
    /// bound to a different write set and is never silently skipped.
    Rejected(CommitError),
    /// The replayed delete targeted a key whose collection differs from the
    /// one the source recorded.
    KeyMismatch,
    /// The outcome stayed undecided after durable resolution.
    Pending,
}

/// Reports whether one derived cursor is at least as advanced as another.
fn cursor_progression_is_valid(previous: &ReplicationCursor, derived: &ReplicationCursor) -> bool {
    previous.member() == derived.member()
        && sequence_at_least(derived.high_water(), previous.high_water())
        && previous.tenants().all(|tenant| {
            sequence_at_least(derived.tenant_progress(tenant), previous.tenant_progress(tenant))
        })
}

/// Compares two optional sequences for "derived is not behind previous".
fn sequence_at_least(derived: Option<StorageSequence>, previous: Option<StorageSequence>) -> bool {
    match (derived, previous) {
        (Some(derived), Some(previous)) => derived >= previous,
        (None, Some(_)) => false,
        (_, None) => true,
    }
}

/// Returns the later of two optional sequences.
fn max_sequence(
    current: Option<StorageSequence>,
    candidate: Option<StorageSequence>,
) -> Option<StorageSequence> {
    match (current, candidate) {
        (Some(current), Some(candidate)) => Some(current.max(candidate)),
        (Some(current), None) => Some(current),
        (None, candidate) => candidate,
    }
}

/// Returns the document keys one commit touches.
fn written_keys(tenant: &TenantId, commit: &PublishedCommit) -> Vec<(TenantId, DocumentId)> {
    commit
        .writes()
        .iter()
        .map(|write| match write {
            PublishedWrite::Put { document } => document.id().clone(),
            PublishedWrite::Delete { id, .. } => id.clone(),
        })
        .map(|id| (tenant.clone(), id))
        .collect()
}

/// Merges one trusted peer stamp into the local clock: the physical
/// component takes the maximum, the logical counter ticks.
fn merge_stamp(local: ReplicationClockStamp, peer: ReplicationClockStamp) -> ReplicationClockStamp {
    ReplicationClockStamp::new(local.ticks().max(peer.ticks()), local.counter().saturating_add(1))
}

/// Returns the members in canonical order.
fn sorted_members(members: &[MemberId]) -> Vec<MemberId> {
    let mut sorted = members.to_vec();
    sorted.sort();
    sorted
}

/// Sorts the operation identities of one conflict record canonically,
/// keeping each clock stamp paired with its operation.
fn sort_record_facts(
    mut operations: Vec<OperationId>,
    mut clocks: Vec<ReplicationClockStamp>,
) -> (Vec<OperationId>, Vec<ReplicationClockStamp>) {
    let mut paired: Vec<(OperationId, ReplicationClockStamp)> =
        operations.drain(..).zip(clocks.drain(..)).collect();
    paired.sort_by(|left, right| left.0.cmp(&right.0));
    let (operations, clocks): (Vec<_>, Vec<_>) = paired.into_iter().unzip();
    (operations, clocks)
}

/// Normalizes one requested level against the degenerate small-Region
/// lattices (ADR-0004 D-4).
///
/// With one Region, `RegionQuorum`, `MajorityRegions`, and `AllRegions`
/// coincide; with two, `MajorityRegions` and `AllRegions` coincide. The
/// canonical reading normalizes to the weakest distinct name the topology
/// actually distinguishes and reports the normalization, so reports stay
/// comparable across topologies.
fn normalize_level(
    level: AcknowledgementLevel,
    region_count: usize,
) -> (AcknowledgementLevel, Option<(AcknowledgementLevel, AcknowledgementLevel)>) {
    use AcknowledgementLevel::{AllRegions, Local, MajorityRegions, RegionQuorum};
    if level == Local {
        return (level, None);
    }
    if region_count <= 1 {
        let normalization = (level != RegionQuorum).then_some((level, RegionQuorum));
        return (RegionQuorum, normalization);
    }
    if region_count == 2 && level == AllRegions {
        return (MajorityRegions, Some((AllRegions, MajorityRegions)));
    }
    (level, None)
}

/// The quorum size of `total` Members or Regions.
fn quorum_of(total: usize) -> usize {
    total / 2 + 1
}

/// Reports whether the contributing Members satisfy one level under the
/// member's topology.
fn level_satisfied(
    level: AcknowledgementLevel,
    config: &ReplicationConfig,
    contributing: &BTreeSet<MemberId>,
) -> bool {
    use AcknowledgementLevel::{AllRegions, Local, MajorityRegions, RegionQuorum};
    if level == Local {
        return true;
    }
    let topology = config.topology();
    let home_region = topology
        .members()
        .find(|member| contributing.contains(*member))
        .and_then(|member| topology.region_of(member).cloned());
    let Some(home) = home_region else { return false };
    let home_members = topology.members_of_region(&home);
    let home_quorum = home_members.iter().filter(|member| contributing.contains(*member)).count()
        >= quorum_of(home_members.len());
    match level {
        Local => true,
        RegionQuorum => home_quorum,
        MajorityRegions | AllRegions => {
            let regions: Vec<_> = topology.regions().cloned().collect();
            let regions_with_quorum = regions
                .iter()
                .filter(|region| {
                    let members = topology.members_of_region(region);
                    members.iter().filter(|member| contributing.contains(*member)).count()
                        >= quorum_of(members.len())
                })
                .count();
            if level == MajorityRegions {
                regions_with_quorum >= quorum_of(regions.len())
            } else {
                regions_with_quorum == regions.len()
            }
        }
    }
}

/// Returns the strongest level a contributing set actually achieves.
fn strongest_satisfied_level(
    config: &ReplicationConfig,
    contributing: &BTreeSet<MemberId>,
) -> AcknowledgementLevel {
    use AcknowledgementLevel::{AllRegions, Local, MajorityRegions, RegionQuorum};
    for level in [AllRegions, MajorityRegions, RegionQuorum] {
        if level_satisfied(level, config, contributing) {
            return level;
        }
    }
    Local
}

/// One observable below-epoch batch this member fenced.
#[derive(Clone, Debug, Eq, PartialEq)]
pub(crate) struct FencedBatch {
    range: String,
    batch_epoch: u64,
    highest_epoch: u64,
    source: MemberId,
}

#[allow(
    dead_code,
    reason = "observed by the W4-REPLICATION R-2 lane tests; the production acquisition seam lands with R-3"
)]
impl FencedBatch {
    /// Records one fenced batch from its facts.
    fn new(range: String, batch_epoch: u64, highest_epoch: u64, source: MemberId) -> Self {
        Self { range, batch_epoch, highest_epoch, source }
    }

    /// Returns the range the fenced batch claimed.
    pub(crate) fn range(&self) -> &str {
        &self.range
    }

    /// Returns the epoch the batch carried.
    pub(crate) fn batch_epoch(&self) -> u64 {
        self.batch_epoch
    }

    /// Returns the highest epoch this member had seen for the range.
    pub(crate) fn highest_epoch(&self) -> u64 {
        self.highest_epoch
    }

    /// Returns the member that pushed the fenced batch.
    pub(crate) fn source(&self) -> &MemberId {
        &self.source
    }
}

/// One observable ownership transition that deposed this member.
#[derive(Clone, Debug, Eq, PartialEq)]
pub(crate) struct Deposition {
    range: String,
    from_epoch: u64,
    to_epoch: u64,
    new_owner: MemberId,
}

#[allow(
    dead_code,
    reason = "observed by the W4-REPLICATION R-2 lane tests; the production acquisition seam lands with R-3"
)]
impl Deposition {
    /// Records one deposition from its facts.
    fn new(range: String, from_epoch: u64, to_epoch: u64, new_owner: MemberId) -> Self {
        Self { range, from_epoch, to_epoch, new_owner }
    }

    /// Returns the range this member lost ownership of.
    pub(crate) fn range(&self) -> &str {
        &self.range
    }

    /// Returns the epoch the ownership was held at before the transition.
    pub(crate) fn held_epoch(&self) -> u64 {
        self.from_epoch
    }

    /// Returns the epoch the new owner holds the range at.
    pub(crate) fn owner_epoch(&self) -> u64 {
        self.to_epoch
    }

    /// Returns the member that owns the range now.
    pub(crate) fn new_owner(&self) -> &MemberId {
        &self.new_owner
    }
}

/// The observation of one acknowledged write at the loss of entire Regions
/// (ADR-0004 D-4; lane-scoped).
///
/// States durable visibility — which Regions and Members durably hold the
/// operation at the loss instant — and the recovery limit: the lost Regions'
/// state is unknown until one of their Members rejoins and validates its
/// cursor. It supports no claim about what the lost Regions held.
#[derive(Clone, Debug, Eq, PartialEq)]
pub(crate) struct RegionLossObservation {
    operation: OperationId,
    acknowledged: AcknowledgementLevel,
    durably_held_by: Vec<(RegionId, Vec<MemberId>)>,
    unknown_in: Vec<RegionId>,
    observed_at: ReplicationInstant,
    limit: RegionLossLimit,
}

#[allow(
    dead_code,
    reason = "observed by the W4-REPLICATION R-2 lane tests; the production acquisition seam lands with R-3"
)]
impl RegionLossObservation {
    /// Returns the operation the observation is about.
    pub(crate) fn operation(&self) -> &OperationId {
        &self.operation
    }

    /// Returns the level the write had been acknowledged at before the loss.
    pub(crate) fn acknowledged(&self) -> AcknowledgementLevel {
        self.acknowledged
    }

    /// Returns the Regions and Members that durably hold the operation, in
    /// canonical order.
    pub(crate) fn durably_held_by(&self) -> &[(RegionId, Vec<MemberId>)] {
        &self.durably_held_by
    }

    /// Returns the lost Regions whose state the observation cannot state.
    pub(crate) fn unknown_in(&self) -> &[RegionId] {
        &self.unknown_in
    }

    /// Returns the lane instant this observation was taken at.
    pub(crate) fn observed_at(&self) -> ReplicationInstant {
        self.observed_at
    }

    /// Returns the stated recovery limit.
    pub(crate) fn recovery_limit(&self) -> RegionLossLimit {
        self.limit
    }
}

/// What a Region-loss observation cannot yet state.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum RegionLossLimit {
    /// The lost Regions' latest state is unknown until one of their Members
    /// rejoins and validates its cursor; nothing about their content may be
    /// inferred before that.
    UnknownUntilRejoin,
}

/// Observes convergence for one operation across a set of members at an
/// injected lane instant.
///
/// This is the convergence seam: an observation taken by the caller, never a
/// value inferred from an acknowledgement or a health status. Every observed
/// Member that durably holds the operation is named with its own local
/// publication fact; Members that do not hold it are simply not named as
/// holding it.
#[allow(
    dead_code,
    reason = "observed by the W4-REPLICATION R-2 lane tests; the production acquisition seam lands with R-3"
)]
pub(crate) fn observe_convergence(
    operation: &OperationId,
    observers: &[(&ReplicationMember, &TenantScope)],
    at: ReplicationInstant,
) -> Convergence {
    let mut holdings: Vec<(MemberId, AcknowledgementLevel)> = observers
        .iter()
        .filter(|(member, scope)| {
            member.engine().is_some_and(|engine| {
                matches!(
                    engine.resolve_commit(scope, operation.clone()),
                    Ok(DurableResolution::Committed(_))
                )
            })
        })
        .map(|(member, _)| (member.id().clone(), AcknowledgementLevel::Local))
        .collect();
    holdings.sort_by(|left, right| left.0.cmp(&right.0));
    Convergence::new(operation.clone(), holdings, at)
}

/// Drives bounded rounds of the deterministic catch-up exchange until the
/// follower reaches the source's last-known high water.
///
/// This is the R-1 anti-entropy driver: one round is a request, a pump, a
/// serve, a pump, and an apply. It never blocks a local commit — it runs
/// only when the caller drives it — and it reports progress facts, not
/// convergence.
///
/// # Errors
///
/// Propagates the member errors of any round.
#[allow(
    dead_code,
    reason = "driven and observed by the W4-REPLICATION R-1 lane tests; the production acquisition seam lands with R-2/R-3"
)]
pub(crate) fn synchronize(
    network: &mut ReplicationNetwork,
    source: &mut ReplicationMember,
    source_scope: &TenantScope,
    follower: &mut ReplicationMember,
    follower_scope: &TenantScope,
    max_rounds: usize,
) -> Result<ApplyReport, MemberError> {
    let mut report = ApplyReport::default();
    for _ in 0..max_rounds {
        let source_id = source.id().clone();
        follower.request_catch_up(network, &source_id, follower_scope)?;
        network.pump();
        source.serve_catch_up(network, source_scope)?;
        network.pump();
        report = follower.apply_deliveries(network, follower_scope)?;
        if report.caught_up() {
            return Ok(report);
        }
    }
    Ok(report)
}
