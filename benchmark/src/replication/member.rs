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
use super::retention::{
    RetentionAdvance, RetentionCompaction, RetentionConsumer, RetentionRefusal,
    SharedTombstoneRetention, TombstoneRetention,
};
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
use crate::families::{
    FamilyError, FamilyMergeError, FamilyWriteRequest, RESERVED_COLLECTION, is_counter_record,
    merge_counter_values,
};
use crate::storage::{PublishedCommit, PublishedWrite};
use crate::types::{
    Document, DocumentId, ExpectedVersion, LocalDurability, OperationId, StorageSequence, TenantId,
    TenantScope,
};

/// The bounded number of commits one catch-up round requests by default.
pub(crate) const DEFAULT_CATCH_UP_BATCH: u32 = 32;

/// The page size of the journal extraction a history-bound declaration
/// performs (GAP-R02): the window is read in bounded pages so a wide
/// declaration never materializes an unbounded read.
const WINDOW_PAGE_LIMIT: u32 = 1024;

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

/// The maximum number of entries each remaining observation log retains
/// (depositions, fenced batches, catch-up fallbacks).
///
/// The same anti-flood rationale as the denial and conflict logs: a
/// flooding control plane or hostile peer can grow each log to this bound
/// and no further, evicting the oldest entry.
pub(crate) const MAX_OBSERVATION_LOGS: usize = 64;

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
    /// Advisory, in-memory catch-up progress per tenant AND serving member,
    /// expressed in that source's storage-sequence space. It never survives a
    /// restart and is never a durability fact; the journal-derived cursor is.
    /// Keying by source is what makes resumption source-matched: one source's
    /// cursor masks only that source's redelivery, never a new source's
    /// commits after a leader transition.
    advisory: BTreeMap<(TenantId, MemberId), SourceProgress>,
    /// The member's bounded hybrid logical clock.
    hlc: ReplicationClockStamp,
    /// The clock stamp each locally admitted operation was minted at.
    operation_stamps: BTreeMap<OperationId, ReplicationClockStamp>,
    /// The data range each locally admitted operation wrote under.
    operation_ranges: BTreeMap<OperationId, String>,
    /// Each operation's journal sequence, recorded when it was committed or
    /// applied, so the acknowledgement path can locate an operation by
    /// identity without scanning the journal head.
    operation_sequences: BTreeMap<OperationId, StorageSequence>,
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
    /// The tombstone retention watermark (GAP-R02): the retention floor,
    /// the retained deletion evidence, and the consumer floors this member
    /// learns from the catch-up requests it answers. The shared handle is
    /// the one watermark a backup plan or migration driver holds too.
    retention: SharedTombstoneRetention,
    /// The observable deletion-evidence servings this member performed.
    evidence_servings: Vec<EvidenceServing>,
    activated: bool,
    answered: bool,
}

/// The recorded state of one document key.
#[derive(Clone, Debug, Eq, PartialEq)]
struct KeyState {
    /// The clock stamp of the last write the key carries.
    stamp: ReplicationClockStamp,
    /// The member whose write this state records: this member for its own
    /// writes, the delivery source for a remote-applied write. The D-3 total
    /// order compares `(stamp, writer)`, and remote-applied states compete
    /// exactly like own writes — a third replica holding two remote writes
    /// converges by that order, not by arrival order.
    writer: MemberId,
    /// The operation that wrote it.
    operation: OperationId,
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
            operation_sequences: BTreeMap::new(),
            operation_ranges: BTreeMap::new(),
            key_states: BTreeMap::new(),
            highest_epoch_seen: BTreeMap::new(),
            conflicts: Vec::new(),
            denials: Vec::new(),
            fallbacks: Vec::new(),
            fenced: Vec::new(),
            depositions: Vec::new(),
            last_rejection: None,
            retention: SharedTombstoneRetention::new(),
            evidence_servings: Vec::new(),
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

    /// Returns a point-in-time snapshot of the member's tombstone
    /// retention watermark.
    ///
    /// The watermark's transitions are explicit typed operations
    /// ([`Self::advance_tombstone_retention`],
    /// [`Self::declare_history_bound`],
    /// [`Self::observe_retention_consumer`]), never a side effect of the
    /// data plane; a snapshot is a copy, and the live state moves only
    /// through those operations.
    pub(crate) fn tombstone_retention(&self) -> TombstoneRetention {
        self.retention.snapshot()
    }

    /// Returns the shared handle to the member's watermark.
    ///
    /// The handle is the one watermark: a backup plan that carries it
    /// reads the member's retained evidence and records its chain's anchor
    /// in the same state the member's own transitions move.
    pub(crate) fn shared_tombstone_retention(&self) -> SharedTombstoneRetention {
        self.retention.clone()
    }

    /// Advances the tombstone retention floor.
    ///
    /// # Errors
    ///
    /// Returns the watermark's typed refusal — a consumer still needs
    /// evidence below the candidate, the candidate regresses the floor, or
    /// a configured cap would be passed — and the floor does not move.
    pub(crate) fn advance_tombstone_retention(
        &mut self,
        candidate: StorageSequence,
    ) -> Result<RetentionAdvance, RetentionRefusal> {
        self.retention.with_lock(|watermark| watermark.advance_floor(candidate))
    }

    /// Declares that this member's publication history below `bound` has
    /// been given up, extracting the window's deletion evidence from the
    /// journal first.
    ///
    /// The extraction is the D2 rule: history is not given up until the
    /// deletion evidence of the window is handed to the watermark, and the
    /// `durably_held` assertion is what makes the retained set provable
    /// after the journal no longer covers it. No engine component gives up
    /// journal history today, so this is the seam the storage compaction
    /// path (GAP-S1) will drive.
    ///
    /// # Errors
    ///
    /// Fails closed with [`DeclareBoundError::Engine`] when the window's
    /// commits cannot be extracted — the declaration is refused rather than
    /// proceeding with an empty extraction, because a window whose facts
    /// were never read is a window whose evidence was silently dropped —
    /// and with [`DeclareBoundError::Refusal`] for the watermark's typed
    /// refusal (a regressing bound, or evidence that is not durably held).
    /// A refused declaration does not move the bound.
    pub(crate) fn declare_history_bound(
        &mut self,
        bound: StorageSequence,
        durably_held: bool,
    ) -> Result<RetentionCompaction, DeclareBoundError> {
        let from =
            self.retention.with_lock(|watermark| watermark.history_bound()).unwrap_or_default();
        let window = self.window_stamped_commits(from, bound)?;
        self.retention
            .with_lock(|watermark| {
                watermark.declare_history_bound_from_commits(bound, &window, durably_held)
            })
            .map_err(DeclareBoundError::Refusal)
    }

    /// Records one consumer floor on the watermark: a backup chain's base
    /// boundary or a migration's read floor.
    ///
    /// Member consumers are recorded automatically from the catch-up
    /// requests this member answers; [`Self::seed_retention_consumers`]
    /// seats the rest.
    pub(crate) fn observe_retention_consumer(
        &mut self,
        consumer: RetentionConsumer,
        floor: Option<StorageSequence>,
    ) -> super::retention::ConsumerObservation {
        self.retention.with_lock(|watermark| watermark.observe_consumer(consumer, floor))
    }

    /// Seeds the watermark's member consumers from the seated topology.
    ///
    /// Every member the view seats is registered for every committed
    /// tenant at its known floor — `None` when it has never requested that
    /// tenant — so an offline member holds the watermark from the moment it
    /// is seated, not from the moment it first talks. Seeding never lowers
    /// a recorded floor.
    ///
    /// # Errors
    ///
    /// Returns [`MemberError::EngineClosed`] and the engine's typed
    /// failures.
    pub(crate) fn seed_retention_consumers(&mut self) -> Result<(), MemberError> {
        let engine = self.engine.as_ref().ok_or(MemberError::EngineClosed)?;
        let tenants = engine.committed_tenants().map_err(MemberError::from)?;
        let seated = self
            .config
            .topology()
            .members()
            .filter(|member| *member != &self.id)
            .cloned()
            .collect::<Vec<_>>();
        for member in seated {
            for tenant in &tenants {
                let known = self
                    .advisory
                    .get(&(tenant.clone(), member.clone()))
                    .and_then(|progress| progress.applied_through);
                // A member that has never requested this tenant is recorded
                // with no floor: it needs all of that tenant's evidence.
                self.retention.with_lock(|watermark| {
                    watermark.observe_consumer(
                        RetentionConsumer::Member {
                            member: member.clone(),
                            tenant: tenant.clone(),
                        },
                        known,
                    );
                });
            }
        }
        Ok(())
    }

    /// Returns the observable deletion-evidence servings this member
    /// performed.
    pub(crate) fn evidence_servings(&self) -> &[EvidenceServing] {
        &self.evidence_servings
    }

    /// Returns the oldest sequence this member can serve history from: the
    /// engine's journal bound, raised by any history bound the watermark
    /// declared.
    ///
    /// # Errors
    ///
    /// Returns [`MemberError::EngineClosed`] and the engine's typed
    /// failures.
    pub(crate) fn effective_servable_start(&self) -> Result<Option<StorageSequence>, MemberError> {
        let engine = self.engine.as_ref().ok_or(MemberError::EngineClosed)?;
        let journal_start = engine.servable_history_start().map_err(MemberError::from)?;
        let declared = self.retention.with_lock(|watermark| watermark.history_bound());
        Ok(journal_start.max(declared))
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
        self.record_operation_sequence(&write.operation, receipt.sequence());
        for key in keys {
            self.key_states.insert(
                key,
                KeyState { stamp, writer: self.id.clone(), operation: write.operation.clone() },
            );
        }
        self.activated = true;
        Ok(receipt)
    }

    /// Commits one typed family write request locally (GAP-F07).
    ///
    /// The write stages through the member's own Engine family seam — the
    /// same path a serving host uses — so the record value it publishes is
    /// the merge encoding of `docs/families/counter-merge-encoding.md` and
    /// the commit rides the publication journal like any document write.
    /// Admission fencing applies exactly as for document writes (D-1): the
    /// member must own the addressed range, the write is stamped with the
    /// member's bounded hybrid logical clock, and the stamp is recorded for
    /// the range and every record key the commit touched, so non-merging
    /// family records (decision D4) compete under D-3 like documents.
    ///
    /// # Errors
    ///
    /// Returns [`MemberError::NotOwner`] when this member does not own the
    /// addressed range, [`MemberError::EngineClosed`] when the engine is
    /// closed, [`MemberError::ApplyFailed`] with
    /// [`ApplyFailure::FamilyStage`] when a family rule rejects the write,
    /// and the engine's typed failures otherwise.
    pub(crate) fn commit_family_write(
        &mut self,
        scope: &TenantScope,
        range: &str,
        request: FamilyWriteRequest,
    ) -> Result<crate::engine::CommitReceipt, MemberError> {
        if self.config.topology().owner_of(range) != Some(&self.id) {
            return Err(MemberError::NotOwner { range: range.to_owned() });
        }
        let stamp = self.tick_local_clock();
        let operation = request.operation_id().clone();
        let engine = self.engine.as_ref().ok_or(MemberError::EngineClosed)?;
        let receipt = commit_family_locally(engine, scope, request)?;
        let keys = journaled_keys(engine, scope.tenant(), receipt.sequence())?;
        self.operation_stamps.insert(operation.clone(), stamp);
        self.operation_ranges.insert(operation.clone(), range.to_owned());
        self.record_operation_sequence(&operation, receipt.sequence());
        for key in keys {
            self.key_states.insert(
                key,
                KeyState { stamp, writer: self.id.clone(), operation: operation.clone() },
            );
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
            if level_satisfied(canonical, &self.config, &self.id, &contributing) {
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
            if !level_satisfied(canonical, &self.config, &self.id, &possible) {
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
                let strongest = strongest_satisfied_level(&self.config, &self.id, &contributing);
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
        let stamp = self.operation_stamps.get(operation).copied().unwrap_or(self.hlc);
        let matches = |commit: &PublishedCommit| commit.operation_id() == operation;
        // Exact window first: the operation's journal sequence is recorded
        // when it was committed or applied, so one bounded read serves
        // `after` inclusive of the sequence itself (the read is exclusive of
        // `after`, hence the saturating step down).
        if let Some(sequence) = self.operation_sequences.get(operation).copied() {
            let after = StorageSequence::new(sequence.get().saturating_sub(1));
            if let Some(commit) =
                engine.tenant_commits(tenant, after, 1).ok()?.into_iter().find(matches)
            {
                return Some(StampedCommit::new(stamp, commit));
            }
        }
        // Fallback: page the journal forward. Each page advances strictly
        // past the last sequence it served, so the scan terminates at the
        // journal's end. This serves members whose sequence record is absent
        // — for example after a restart — at the cost of a full scan.
        let batch = usize::try_from(MAX_CATCH_UP_BATCH).unwrap_or(usize::MAX);
        let mut after = StorageSequence::new(0);
        loop {
            let page = engine.tenant_commits(tenant, after, MAX_CATCH_UP_BATCH).ok()?;
            let page_len = page.len();
            let last_sequence = page.last().map(PublishedCommit::sequence)?;
            if let Some(commit) = page.into_iter().find(matches) {
                return Some(StampedCommit::new(stamp, commit));
            }
            if page_len < batch {
                return None;
            }
            after = last_sequence;
        }
    }

    /// Records one operation's journal sequence for the exact lookup path.
    /// Records the sequence for an operation into the lookup map.
    ///
    /// For operations this member admitted, the sequence is this member's
    /// own journal sequence. For remote-applied operations the recorded
    /// value is the SOURCE journal's sequence (source sequence space): the
    /// exact-window lookup then misses and the paged fallback serves, which
    /// is correct but slower. The acknowledgement path only looks up
    /// admitted operations, so this stays a latency note, not a correctness
    /// one.
    fn record_operation_sequence(&mut self, operation: &OperationId, sequence: StorageSequence) {
        self.operation_sequences.insert(operation.clone(), sequence);
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
            .get(&(scope.tenant().clone(), source.clone()))
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
    #[allow(
        clippy::too_many_lines,
        reason = "one serving pass: consumer floors, the tombstone half of a below-bound catch-up, and the batch itself"
    )]
    pub(crate) fn serve_catch_up(
        &mut self,
        network: &mut ReplicationNetwork,
        scope: &TenantScope,
    ) -> Result<usize, MemberError> {
        let tenant = scope.tenant().clone();
        self.refresh_clock_from(network.now());
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
            // Every answered request is a consumer-floor observation: this
            // member learns each requester's durable progress in its own
            // sequence space, which is what the retention floor is bounded
            // by (GAP-R02 decision D3).
            self.retention.with_lock(|watermark| {
                watermark.observe_consumer(
                    RetentionConsumer::Member { member: requester.clone(), tenant: tenant.clone() },
                    after,
                );
            });
            let servable_from = self.effective_servable_start()?;
            let gap_below = catch_up_gap(after, servable_from);
            // The tombstone half of a below-bound catch-up: when the
            // watermark holds the deletion evidence above the requester's
            // cursor and the window holds no put it missed, the retained
            // facts complete the requester's history below the bound and
            // the batch above serves from the bound instead of naming a
            // gap.
            let mut serving_after = after;
            let mut gap_for_batch = gap_below;
            if gap_below.is_some() {
                let cursor = after.unwrap_or_default();
                let covered = self.retention.with_lock(|watermark| {
                    let floor = watermark.retention_floor();
                    floor.get() <= cursor.get()
                        && watermark
                            .non_deletion_through(&tenant)
                            .is_none_or(|through| through.get() <= cursor.get())
                });
                if covered {
                    let (evidence, floor, non_deletion_through) =
                        self.retention.with_lock(|watermark| {
                            (
                                watermark.deletion_evidence_above(&tenant, cursor),
                                watermark.retention_floor(),
                                watermark.non_deletion_through(&tenant),
                            )
                        });
                    let bound = servable_from.unwrap_or_default();
                    network
                        .send(
                            &self.id,
                            &requester,
                            ReplicationFrame::DeletionEvidenceBatch {
                                source: self.id.clone(),
                                tenant: tenant.clone(),
                                evidence,
                                servable_from: bound,
                                retention_floor: floor,
                                non_deletion_through,
                            },
                        )
                        .map_err(|_| MemberError::TransportFull)?;
                    self.record_evidence_serving(EvidenceServing::new(
                        tenant.clone(),
                        requester.clone(),
                        bound,
                    ));
                    serving_after = Some(StorageSequence::new(bound.get().saturating_sub(1)));
                    gap_for_batch = None;
                }
            }
            let batch = build_catch_up_batch(
                self.engine.as_ref().ok_or(MemberError::EngineClosed)?,
                &self.id,
                &tenant,
                serving_after,
                limit,
                &self.operation_stamps,
                self.hlc,
                servable_from,
                gap_for_batch,
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
        // The tombstone half of the pass: retained evidence applied below
        // the source's history bound (GAP-R02), counted separately from
        // journaled commits so a report can name an evidence-only pass.
        let mut evidence_applied = 0;
        let mut evidence_duplicates = 0;
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
                            .get(&(tenant.clone(), source.clone()))
                            .and_then(|progress| progress.applied_through);
                        if advisory_cursor == Some(gap_cursor) && gap_cursor.get() > 0 {
                            // The source cannot serve below this member's own
                            // cursor under that source: fall back to a full
                            // catch-up, never silently skip the gap.
                            self.advisory.remove(&(tenant.clone(), source.clone()));
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
                ReplicationFrame::DeletionEvidenceBatch {
                    source,
                    tenant: evidence_tenant,
                    evidence,
                    servable_from,
                    retention_floor,
                    non_deletion_through,
                } => {
                    if evidence_tenant != tenant {
                        self.record_denial(TenantDenial::new(
                            DenialSeam::ApplyDelivery,
                            tenant.clone(),
                            evidence_tenant.clone(),
                            source.clone(),
                        ));
                        return Err(MemberError::CrossTenant {
                            scope: tenant,
                            attempted: evidence_tenant,
                        });
                    }
                    // The retained window must cover this member's cursor.
                    // A source whose retention floor sits above the cursor
                    // cannot prove the deletions this member missed, and a
                    // put this member missed below the bound is not
                    // reconstructable from evidence at all — both are the
                    // typed gap failure, never a silent skip.
                    let cursor = self
                        .advisory
                        .get(&(tenant.clone(), source.clone()))
                        .and_then(|progress| progress.applied_through)
                        .unwrap_or_default();
                    if retention_floor.get() > cursor.get()
                        || non_deletion_through.is_some_and(|through| through.get() > cursor.get())
                    {
                        return Err(MemberError::GapUnservable {
                            tenant: tenant.clone(),
                            cursor,
                            servable_from,
                        });
                    }
                    let mut applied = 0;
                    let mut duplicates = 0;
                    for fact in &evidence {
                        // One retained fact replays as the delete it
                        // published: the same ledger identity, version
                        // binding, and key-state recording as a journaled
                        // delivery, so a duplicate evidence delivery
                        // resolves by identity and never deletes a key a
                        // different write owns.
                        let stamped = StampedCommit::new(
                            fact.stamp(),
                            PublishedCommit::new(
                                tenant.clone(),
                                fact.operation().clone(),
                                fact.sequence(),
                                fact.durability(),
                                vec![PublishedWrite::Delete {
                                    collection: fact.collection().cloned(),
                                    id: fact.id().clone(),
                                    version_at_delete: fact.version_at_delete(),
                                }],
                            ),
                        );
                        match self.apply_one(scope, &stamped, &source)? {
                            ApplyStep::Applied => applied += 1,
                            ApplyStep::Duplicate => duplicates += 1,
                        }
                    }
                    // The evidence completed this member's history below
                    // the bound (no put was missed), so its advisory
                    // progress for this source may move to the bound: the
                    // next request resumes incrementally from there.
                    let progress = self.advisory.entry((tenant.clone(), source.clone())).or_insert(
                        SourceProgress {
                            source: source.clone(),
                            applied_through: None,
                            tenant_high_water: None,
                        },
                    );
                    progress.applied_through = max_sequence(
                        progress.applied_through,
                        Some(StorageSequence::new(servable_from.get().saturating_sub(1))),
                    );
                    evidence_applied += applied;
                    evidence_duplicates += duplicates;
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
        report.applied += evidence_applied;
        report.duplicates_resolved += evidence_duplicates;
        // The pass merge is keyed by (source, sequence): two sources' journals
        // are independent sequence spaces, and one source's sequence-1 commit
        // must never overwrite another's.
        let mut merged: BTreeMap<(MemberId, u64), (&StampedCommit, &MemberId)> = BTreeMap::new();
        for batch in &batches {
            for stamped in &batch.commits {
                if merged
                    .insert(
                        (batch.source.clone(), stamped.commit.sequence().get()),
                        (stamped, &batch.source),
                    )
                    .is_some()
                {
                    report.duplicates_resolved += 1;
                }
            }
            report.tenant_high_water =
                max_sequence(report.tenant_high_water, batch.tenant_high_water);
        }
        for ((source_id, sequence), (stamped, source)) in merged {
            let sequence = StorageSequence::new(sequence);
            // Source-matched resumption: only THIS source's advisory cursor
            // masks this source's redelivery. A new source's commits apply
            // regardless of how far the member progressed under a previous
            // source — the durable operation ledger and the D-3 policy are
            // what make re-delivery safe, not the cursor.
            let resumed_from = self
                .advisory
                .get(&(tenant.clone(), source_id.clone()))
                .and_then(|progress| progress.applied_through);
            if let Some(resumed) = resumed_from
                && sequence <= resumed
            {
                report.duplicates_resolved += 1;
                continue;
            }
            // A self round-trip: this member's own write returned to it
            // through another member's journal, carrying the stamp it was
            // minted with. The member's own ledger entry is authoritative,
            // so the delivery resolves by identity instead of staging — and
            // because the stamp must match the one this member minted, a
            // hostile delivery reusing a live identity under a different
            // stamp still fails the typed check below.
            let own_stamp = self.operation_stamps.get(stamped.commit.operation_id()).copied();
            let already_committed = self.engine.as_ref().is_some_and(|engine| {
                matches!(
                    engine.resolve_commit(scope, stamped.commit.operation_id().clone()),
                    Ok(DurableResolution::Committed(_))
                )
            });
            if own_stamp == Some(stamped.stamp) && already_committed {
                report.duplicates_resolved += 1;
            } else {
                match self.resolve_incoming(source, stamped, &tenant) {
                    Incoming::ConflictSkipped => {
                        report.conflicts_recorded += 1;
                    }
                    Incoming::Apply => match self.apply_one(scope, stamped, source)? {
                        ApplyStep::Applied => report.applied += 1,
                        ApplyStep::Duplicate => report.duplicates_resolved += 1,
                    },
                }
            }
            let progress = self.advisory.entry((tenant.clone(), source_id.clone())).or_insert(
                SourceProgress {
                    source: source_id.clone(),
                    applied_through: None,
                    tenant_high_water: None,
                },
            );
            progress.applied_through = max_sequence(progress.applied_through, Some(sequence));
        }
        for batch in &batches {
            let progress = self.advisory.entry((tenant.clone(), batch.source.clone())).or_insert(
                SourceProgress {
                    source: batch.source.clone(),
                    applied_through: None,
                    tenant_high_water: None,
                },
            );
            progress.tenant_high_water =
                max_sequence(progress.tenant_high_water, batch.tenant_high_water);
        }
        let last_source = batches.last().map(|batch| batch.source.clone());
        let progress = last_source
            .as_ref()
            .and_then(|source| self.advisory.get(&(tenant.clone(), source.clone())));
        if let Some(progress) = progress {
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

    /// Decides the D-3 disposition of one incoming commit against the
    /// member's recorded key states and clock, recording the conflict either
    /// way.
    ///
    /// The comparison is a total order on `(stamp, Member)`, so it decides
    /// identically on both sides of a pair and convergence is symmetric —
    /// including when the competing state came from a REMOTE write this
    /// member applied earlier: a third replica holding two remote writes
    /// converges by that order, never by arrival order. Because an operation
    /// publishes atomically, one disposition covers all of its keys: the
    /// operation applies only when its write beats every competing key state.
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
        // Every recorded state of the touched keys competes, regardless of
        // whether this member or a remote peer wrote it — except counter
        // family records, which merge instead of competing (GAP-F07
        // decision D1): an incoming counter write always applies, so a
        // concurrent increment is never conflict-skipped.
        let competing: Vec<(&(TenantId, DocumentId), &KeyState)> = self
            .key_states
            .iter()
            .filter(|(key, _)| keys.contains(key) && !is_counter_record(&key.1))
            .collect();
        if competing.is_empty() {
            return Incoming::Apply;
        }
        let incoming: (&ReplicationClockStamp, &MemberId) = (&stamp, source);
        let beats_every =
            competing.iter().all(|(_, state)| incoming > (&state.stamp, &state.writer));
        // The winner the record names: the incoming write when it beats every
        // competing state, otherwise the strongest competing state — which
        // may belong to a remote writer, exactly the third-replica case.
        let mut operations: Vec<OperationId> =
            competing.iter().map(|(_, state)| state.operation.clone()).collect();
        let mut clocks: Vec<ReplicationClockStamp> =
            competing.iter().map(|(_, state)| state.stamp).collect();
        operations.push(stamped.commit.operation_id().clone());
        clocks.push(stamp);
        let (operations, clocks) = sort_record_facts(operations, clocks);
        if beats_every {
            // The incoming write wins every competing key: it applies, and
            // the losing writes are retained as history.
            self.record_conflict(ConflictRecord::new(
                operations,
                sorted_members(&[source.clone(), self.id.clone()]),
                clocks,
                ConflictOutcome::Winner(source.clone()),
            ));
            Incoming::Apply
        } else {
            // At least one recorded state wins (or the deterministic tie
            // favors it), so the atomic operation is skipped and the
            // incoming write is retained as history.
            let winner = competing
                .iter()
                .map(|(_, state)| state)
                .max_by_key(|state| (state.stamp, state.writer.clone()))
                .map(|state| state.writer.clone())
                .expect("competing states are nonempty");
            let mut members = vec![source.clone(), winner.clone()];
            for (_, state) in &competing {
                members.push(state.writer.clone());
            }
            self.record_conflict(ConflictRecord::new(
                operations,
                sorted_members(&members),
                clocks,
                ConflictOutcome::Winner(winner),
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
    #[allow(
        clippy::too_many_lines,
        reason = "one apply pass: identity dedup, counter merge, staging, and commit accounting"
    )]
    fn apply_one(
        &mut self,
        scope: &TenantScope,
        stamped: &StampedCommit,
        source: &MemberId,
    ) -> Result<ApplyStep, MemberError> {
        let commit = &stamped.commit;
        let engine = self.engine.as_ref().ok_or(MemberError::EngineClosed)?;
        let already_committed = matches!(
            engine
                .resolve_commit(scope, commit.operation_id().clone())
                .map_err(MemberError::from)?,
            DurableResolution::Committed(_)
        );
        // Identity-scoped delta dedup for merged counter records (GAP-F07
        // decision D3): the first application of this operation already
        // merged its effects into the local record, so a redelivery —
        // duplicate frames, a source's retry, another member's journal —
        // resolves by identity and stages nothing. Restaging a merged value
        // under a live identity would trip the operation ledger's
        // identity->write-set guard once later operations merged on top;
        // resolving by identity is also the fail-closed answer to a hostile
        // delivery reusing the identity with different contents: the
        // durable first binding wins and the hostile value is dropped,
        // never merged.
        if already_committed && commit.writes().iter().any(is_mergeable_counter_write) {
            self.operation_stamps.entry(commit.operation_id().clone()).or_insert(stamped.stamp);
            self.record_operation_sequence(commit.operation_id(), commit.sequence());
            for key in written_keys(scope.tenant(), commit) {
                self.key_states.insert(
                    key,
                    KeyState {
                        stamp: stamped.stamp,
                        writer: source.clone(),
                        operation: commit.operation_id().clone(),
                    },
                );
            }
            return Ok(ApplyStep::Duplicate);
        }
        let snapshot = engine.read_snapshot(scope).map_err(MemberError::from)?;
        let mut transaction = engine
            .begin(scope, snapshot, commit.operation_id().clone())
            .map_err(MemberError::from)?;
        let mut staged_anything = false;
        for write in commit.writes() {
            match write {
                PublishedWrite::Put { document } => {
                    let staged =
                        mergeable_counter_put(document, &mut transaction).map_err(|reason| {
                            MemberError::ApplyFailed {
                                operation: commit.operation_id().clone(),
                                reason,
                            }
                        })?;
                    let document = match staged {
                        Some(merged) => merged,
                        None => document.clone(),
                    };
                    transaction.put(document, ExpectedVersion::Any).map_err(MemberError::from)?;
                    staged_anything = true;
                }
                PublishedWrite::Delete { collection, id, version_at_delete } => {
                    // A counter-record delete is the source's empty record
                    // state: under merge the union keeps the local record,
                    // so the delete stages nothing (decision record D1 —
                    // record clears carry no merge information).
                    if is_counter_record(id)
                        && collection
                            .as_ref()
                            .is_some_and(|name| name.as_str() == RESERVED_COLLECTION)
                    {
                        continue;
                    }
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
                    staged_anything = true;
                }
            }
        }
        // A delivery whose every write was a counter-record delete staged
        // nothing: there is no transaction to commit, and the operation's
        // effects are already the local state.
        if !staged_anything {
            return Ok(ApplyStep::Duplicate);
        }
        match transaction.commit(LocalDurability::Synced) {
            CommitOutcome::Committed(_) => {
                // The applied write now carries the incoming stamp on every
                // key it touched, recorded with the member the delivery came
                // from so a later competing delivery — from this member or
                // any peer — is compared under the same (stamp, Member)
                // total order. The stamp is recorded too, so a commit this
                // member forwards keeps the stamp its originator minted.
                self.operation_stamps.entry(commit.operation_id().clone()).or_insert(stamped.stamp);
                self.record_operation_sequence(commit.operation_id(), commit.sequence());
                for key in written_keys(scope.tenant(), commit) {
                    self.key_states.insert(
                        key,
                        KeyState {
                            stamp: stamped.stamp,
                            writer: source.clone(),
                            operation: commit.operation_id().clone(),
                        },
                    );
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
                        self.operation_stamps
                            .entry(commit.operation_id().clone())
                            .or_insert(stamped.stamp);
                        self.record_operation_sequence(commit.operation_id(), commit.sequence());
                        for key in written_keys(scope.tenant(), commit) {
                            self.key_states.insert(
                                key,
                                KeyState {
                                    stamp: stamped.stamp,
                                    writer: source.clone(),
                                    operation: commit.operation_id().clone(),
                                },
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
    /// The view must seat this member and must be a valid transition for
    /// every range the member knows about, owned or not: a lowered epoch —
    /// or an owner flip without a raised epoch — is stale anywhere, because
    /// the member's fencing state (`highest_epoch_seen`) must stay
    /// consistent with the view it holds. Losing ownership of a range
    /// records the deposition: from that moment the member's own writes to
    /// the range fail admission (D-1), and its already-committed writes keep
    /// their local receipts and converge under D-3.
    ///
    /// # Errors
    ///
    /// Returns [`MemberError::InvalidTopology`] for an unservable view,
    /// [`MemberError::NotInTopology`] when the view does not seat this
    /// member, and [`MemberError::StaleView`] when any known range's epoch
    /// would go backwards, or an owner would flip without a raised epoch.
    pub(crate) fn apply_view(&mut self, view: TopologyView) -> Result<(), MemberError> {
        let view = view.validated().map_err(|_| MemberError::InvalidTopology)?;
        if !view.contains_member(&self.id) {
            return Err(MemberError::NotInTopology);
        }
        // Fencing consistency first: no known range may regress. The highest
        // epoch seen comes from views AND received pushes, so this holds for
        // ranges this member never owned.
        for (range, highest) in self.highest_epoch_seen.clone() {
            if let Some(offered) = view.owner_epoch_of(&range)
                && offered < highest
            {
                return Err(MemberError::StaleView {
                    range,
                    current_epoch: highest,
                    offered_epoch: offered,
                });
            }
        }
        // Transition validity for every range the current view names: an
        // owner flip requires a raised epoch (D-1).
        let known_ranges: Vec<(String, MemberId, u64)> = self
            .config
            .topology()
            .owned_ranges()
            .filter_map(|range| {
                let owner = self.config.topology().owner_of(range)?.clone();
                let epoch = self.config.topology().owner_epoch_of(range).unwrap_or(1);
                Some((range.to_owned(), owner, epoch))
            })
            .collect();
        for (range, current_owner, current_epoch) in known_ranges {
            let Some(new_owner) = view.owner_of(&range) else { continue };
            let offered_epoch = view.owner_epoch_of(&range).unwrap_or(1);
            let acceptable = if new_owner == &current_owner {
                offered_epoch >= current_epoch
            } else {
                offered_epoch > current_epoch
            };
            if !acceptable {
                return Err(MemberError::StaleView { range, current_epoch, offered_epoch });
            }
            if new_owner != &self.id && current_owner == self.id {
                self.record_deposition(Deposition::new(
                    range,
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

    /// Records one deposition, evicting the oldest beyond the bound.
    fn record_deposition(&mut self, deposition: Deposition) {
        if self.depositions.len() >= MAX_OBSERVATION_LOGS {
            self.depositions.remove(0);
        }
        self.depositions.push(deposition);
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

    /// Records one fenced batch, evicting the oldest beyond the bound.
    fn record_fenced(&mut self, batch: FencedBatch) {
        if self.fenced.len() >= MAX_OBSERVATION_LOGS {
            self.fenced.remove(0);
        }
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
        self.operation_sequences.clear();
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
        self.advisory
            .iter()
            .filter(|((recorded, _), _)| recorded == tenant)
            .filter_map(|(_, progress)| progress.applied_through)
            .max()
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

    /// Records one catch-up fallback, evicting the oldest beyond the bound.
    fn record_fallback(&mut self, fallback: CatchUpFallback) {
        if self.fallbacks.len() >= MAX_OBSERVATION_LOGS {
            self.fallbacks.remove(0);
        }
        self.fallbacks.push(fallback);
    }

    /// Records one deletion-evidence serving, evicting the oldest beyond
    /// the bound.
    fn record_evidence_serving(&mut self, serving: EvidenceServing) {
        if self.evidence_servings.len() >= MAX_OBSERVATION_LOGS {
            self.evidence_servings.remove(0);
        }
        self.evidence_servings.push(serving);
    }

    /// Returns the journal's commits of every tenant in `(from, to)`, the
    /// half-open extraction window a history-bound declaration hands to
    /// the watermark, paired with the stamps this member minted or
    /// recorded for them.
    ///
    /// This is the extraction window a history-bound declaration hands to
    /// the watermark: the commits whose deletion evidence must be retained
    /// before the history below the bound is given up. A commit this
    /// member holds no stamp for is paired with the origin clock, the same
    /// fallback the catch-up batch uses.
    ///
    /// # Errors
    ///
    /// Returns [`MemberError::EngineClosed`] and the engine's typed
    /// failures.
    fn window_stamped_commits(
        &self,
        from: StorageSequence,
        to: StorageSequence,
    ) -> Result<Vec<StampedCommit>, MemberError> {
        let engine = self.engine.as_ref().ok_or(MemberError::EngineClosed)?;
        let mut window = Vec::new();
        for tenant in engine.committed_tenants().map_err(MemberError::from)? {
            let mut after = from;
            loop {
                let page = engine
                    .tenant_commits(&tenant, after, WINDOW_PAGE_LIMIT)
                    .map_err(MemberError::from)?;
                if page.is_empty() {
                    break;
                }
                let reached = page.last().is_none_or(|commit| commit.sequence().get() >= to.get());
                for commit in &page {
                    if commit.sequence().get() >= to.get() {
                        continue;
                    }
                    let stamp = self
                        .operation_stamps
                        .get(commit.operation_id())
                        .copied()
                        .unwrap_or(ReplicationClockStamp::new(0, 0));
                    window.push(StampedCommit::new(stamp, commit.clone()));
                }
                after = page.last().map_or(after, PublishedCommit::sequence);
                if reached {
                    break;
                }
            }
        }
        window.sort_by_key(|stamped| stamped.commit.sequence());
        Ok(window)
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

/// Reports whether one published write is a counter family record put in
/// the reserved collection (GAP-F07 decision D1): the writes that merge.
fn is_mergeable_counter_write(write: &PublishedWrite) -> bool {
    matches!(write, PublishedWrite::Put { document }
        if document.collection().as_str() == RESERVED_COLLECTION && is_counter_record(document.id()))
}

/// Merges one incoming counter-record put against the local record the
/// transaction observes.
///
/// Returns the merged document to stage, or `None` when the write is not a
/// mergeable counter record (the caller stages it unchanged).
///
/// # Errors
///
/// Returns [`ApplyFailure::FamilyMerge`] when either side fails to decode
/// as counter state or the merged total leaves the signed 64-bit range
/// (decision record D1/D3: fail closed, never truncate).
fn mergeable_counter_put(
    document: &Document,
    transaction: &mut crate::engine::Transaction<'_>,
) -> Result<Option<Document>, ApplyFailure> {
    if document.collection().as_str() != RESERVED_COLLECTION || !is_counter_record(document.id()) {
        return Ok(None);
    }
    let local = transaction
        .read(document.id())
        .map_err(|_| ApplyFailure::Rejected(CommitError::StorageFailure))?;
    let local_value = local
        .filter(|existing| existing.collection().as_str() == RESERVED_COLLECTION)
        .map(|existing| existing.value().clone());
    let merged = merge_counter_values(local_value.as_ref(), document.value())
        .map_err(ApplyFailure::FamilyMerge)?;
    Ok(Some(Document::new(document.id().clone(), document.collection().clone(), merged)))
}

/// Commits one family write request through the Engine family seam
/// (GAP-F07): the record value published here is the merge encoding, and
/// the commit rides the publication journal like any document write.
///
/// # Errors
///
/// Returns [`MemberError::ApplyFailed`] with [`ApplyFailure::FamilyStage`]
/// when a family rule rejects the write, and the engine's typed failures
/// otherwise.
fn commit_family_locally(
    engine: &Engine,
    scope: &TenantScope,
    request: FamilyWriteRequest,
) -> Result<crate::engine::CommitReceipt, MemberError> {
    let operation = request.operation_id().clone();
    let snapshot = engine.read_snapshot(scope).map_err(MemberError::from)?;
    let mut transaction =
        engine.begin(scope, snapshot, operation.clone()).map_err(MemberError::from)?;
    engine.families().stage(&mut transaction, request).map_err(|error| {
        MemberError::ApplyFailed {
            operation: operation.clone(),
            reason: ApplyFailure::FamilyStage(error),
        }
    })?;
    match transaction.commit(LocalDurability::Synced) {
        CommitOutcome::Committed(receipt) => Ok(receipt),
        CommitOutcome::Rejected(reason) => {
            Err(MemberError::ApplyFailed { operation, reason: ApplyFailure::Rejected(reason) })
        }
        CommitOutcome::Unknown(unknown) => {
            match engine
                .resolve_commit(scope, unknown.operation_id().clone())
                .map_err(MemberError::from)?
            {
                DurableResolution::Committed(receipt) => Ok(receipt),
                DurableResolution::Rejected(reason) => Err(MemberError::ApplyFailed {
                    operation: unknown.operation_id().clone(),
                    reason: ApplyFailure::Rejected(reason),
                }),
                DurableResolution::Pending => Err(MemberError::ApplyFailed {
                    operation: unknown.operation_id().clone(),
                    reason: ApplyFailure::Pending,
                }),
            }
        }
    }
}

/// Returns the document keys one committed sequence touched, from the
/// publication journal — the generic source of what a family write
/// published, with no family layout knowledge in this module.
///
/// # Errors
///
/// Returns the engine's typed failures.
fn journaled_keys(
    engine: &Engine,
    tenant: &TenantId,
    sequence: StorageSequence,
) -> Result<Vec<(TenantId, DocumentId)>, MemberError> {
    let after = StorageSequence::new(sequence.get().saturating_sub(1));
    let commits = engine.tenant_commits(tenant, after, 1).map_err(MemberError::from)?;
    // The journal is advisory here; a journaled-record miss (the recorded
    // repair-F2 divergence window) reports no keys rather than attributing
    // a neighbor's write set to this commit.
    let Some(commit) = commits.first().filter(|commit| commit.sequence() == sequence) else {
        return Ok(Vec::new());
    };
    Ok(commit
        .writes()
        .iter()
        .map(|write| match write {
            PublishedWrite::Put { document } => document.id().clone(),
            PublishedWrite::Delete { id, .. } => id.clone(),
        })
        .map(|id| (tenant.clone(), id))
        .collect())
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
    clippy::too_many_arguments,
    reason = "one batch assembly: the requester's cursor, the batch bound, the stamps, and the gap fact it resolved"
)]
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
    servable_from: Option<StorageSequence>,
    gap_below: Option<StorageSequence>,
) -> Result<ReplicationFrame, MemberError> {
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

/// One observable deletion-evidence serving (GAP-R02).
///
/// The serving is the tombstone half of a below-bound catch-up: the member
/// answered a requester whose cursor fell below its effective history bound
/// with the retained evidence above that cursor. It is observable so a lane
/// or operator can tell evidence service from a plain batch.
#[derive(Clone, Debug, Eq, PartialEq)]
#[allow(
    dead_code,
    reason = "driven and observed by the GAP-R02 tests; the production acquisition seam lands later"
)]
pub(crate) struct EvidenceServing {
    tenant: TenantId,
    requester: MemberId,
    through: StorageSequence,
}

#[allow(
    dead_code,
    reason = "driven and observed by the GAP-R02 tests; the production acquisition seam lands later"
)]
impl EvidenceServing {
    /// Records one serving from its facts.
    fn new(tenant: TenantId, requester: MemberId, through: StorageSequence) -> Self {
        Self { tenant, requester, through }
    }

    /// Returns the tenant the evidence belonged to.
    pub(crate) fn tenant(&self) -> &TenantId {
        &self.tenant
    }

    /// Returns the member the evidence was served to.
    pub(crate) fn requester(&self) -> &MemberId {
        &self.requester
    }

    /// Returns the history bound the evidence was retained below.
    pub(crate) fn through(&self) -> StorageSequence {
        self.through
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

/// The typed failure of one history-bound declaration (GAP-R02).
///
/// Extraction is part of the declaration, not a precondition of it: a
/// window the engine cannot read fails the whole declaration, because
/// treating an unreadable window as an empty one would give up history
/// whose deletion evidence was never extracted — the exact delayed data
/// loss the watermark exists to prevent.
#[derive(Clone, Debug, Eq, PartialEq)]
pub(crate) enum DeclareBoundError {
    /// The engine's typed failure: the window could not be extracted, so
    /// history was not given up.
    Engine(MemberError),
    /// The watermark refused the declaration; its refusal is recorded in
    /// the watermark's bounded observation log.
    Refusal(RetentionRefusal),
}

impl From<MemberError> for DeclareBoundError {
    fn from(error: MemberError) -> Self {
        Self::Engine(error)
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
    /// A family write staged through the member's Engine was rejected by a
    /// family rule (GAP-F07).
    FamilyStage(FamilyError),
    /// Two counter records could not merge: a malformed record, or a
    /// merged total outside the signed 64-bit range (GAP-F07 decision
    /// record D1/D3). The delivery is never partially applied.
    FamilyMerge(FamilyMergeError),
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

/// Returns the distinct members in canonical order.
fn sorted_members(members: &[MemberId]) -> Vec<MemberId> {
    let mut sorted = members.to_vec();
    sorted.sort();
    sorted.dedup();
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

/// Reports whether the contributing Members satisfy one level for a write
/// whose writer (the admitted owner of the operation's range) is `writer`.
///
/// The write's home Region is the WRITER's Region — the D-1 admission rule
/// makes exactly that Member the write path's owner — never the Region of
/// whichever contributing member happens to be seated first: a foreign
/// Region's member durably recording the operation contributes to no home
/// Region quorum of the writer.
fn level_satisfied(
    level: AcknowledgementLevel,
    config: &ReplicationConfig,
    writer: &MemberId,
    contributing: &BTreeSet<MemberId>,
) -> bool {
    use AcknowledgementLevel::{AllRegions, Local, MajorityRegions, RegionQuorum};
    if level == Local {
        return true;
    }
    let topology = config.topology();
    let Some(home) = topology.region_of(writer).cloned() else { return false };
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

/// Returns the strongest level a contributing set actually achieves for a
/// write by `writer`.
fn strongest_satisfied_level(
    config: &ReplicationConfig,
    writer: &MemberId,
    contributing: &BTreeSet<MemberId>,
) -> AcknowledgementLevel {
    use AcknowledgementLevel::{AllRegions, Local, MajorityRegions, RegionQuorum};
    for level in [AllRegions, MajorityRegions, RegionQuorum] {
        if level_satisfied(level, config, writer, contributing) {
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
