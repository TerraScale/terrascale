//! The crate-private replication member driver.
//!
//! One [`ReplicationMember`] owns one durable [`Engine`], applies one
//! validated [`ReplicationConfig`], and moves committed operations between
//! members through the deterministic transport doubles. It is the R-1 data
//! plane: catch-up from the publication journal, idempotent replay through
//! the W3 operation ledger, a durable [`ReplicationCursor`] derived from the
//! member's own journal, rejoin validation, anti-entropy repair, and tenant
//! isolation at every seam.
//!
//! What R-1 deliberately does not do: it evaluates no acknowledgement level
//! above `Local`, produces no convergence or conflict observation, and runs
//! no leader election or fencing — those belong to the R-2 phase of
//! W4-REPLICATION. The catch-up protocol is at-least-once: duplicates are
//! resolved by operation identity, never by de-duplicating delivery.

use std::collections::BTreeMap;

use super::identifier::validate_topology_identifier;
use super::status::member_health;
use super::transport::{FrameSelector, ReplicationFrame, ReplicationNetwork};
use super::{
    AcknowledgementLevel, MemberId, ReplicationConfig, ReplicationCursor, ReplicationStatus,
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
pub(crate) const MAX_DENIALS: usize = 64;

/// One replication member: one Engine, one configuration, one identity.
pub(crate) struct ReplicationMember {
    id: MemberId,
    engine: Option<Engine>,
    engine_config: EngineConfig,
    config: ReplicationConfig,
    /// Advisory, in-memory catch-up progress per tenant, expressed in the
    /// serving member's storage-sequence space. It never survives a restart
    /// and is never a durability fact; the journal-derived cursor is.
    advisory: BTreeMap<TenantId, SourceProgress>,
    /// The bounded, observable tenant-isolation denial log.
    denials: Vec<TenantDenial>,
    /// The observable catch-up fallbacks this member performed.
    fallbacks: Vec<CatchUpFallback>,
    /// The last rejoin rejection, when rejoin validation refused admission.
    last_rejection: Option<RejoinRejection>,
    activated: bool,
    answered: bool,
}

/// One tenant's advisory progress toward one serving member.
#[derive(Clone, Debug, Eq, PartialEq)]
struct SourceProgress {
    source: MemberId,
    applied_through: Option<StorageSequence>,
    tenant_high_water: Option<StorageSequence>,
}

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
        Ok(Self {
            id,
            engine: Some(engine),
            engine_config,
            config,
            advisory: BTreeMap::new(),
            denials: Vec::new(),
            fallbacks: Vec::new(),
            last_rejection: None,
            activated: false,
            answered: false,
        })
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
    /// R-1 evaluates only [`AcknowledgementLevel::Local`]: a stronger level
    /// is named explicitly and refused with a typed error until the R-2
    /// lattice exists. Under D-1 the member admits the write only while its
    /// current topology view names it owner of the addressed data range, so
    /// a deposed owner's new writes fail admission locally.
    ///
    /// # Errors
    ///
    /// Returns [`MemberError::AcknowledgementLevelNotImplemented`] for any
    /// level above `Local`, [`MemberError::NotOwner`] when this member does
    /// not own the addressed range, and the engine's typed failures
    /// otherwise.
    pub(crate) fn commit_write(
        &mut self,
        scope: &TenantScope,
        write: ReplicationWrite,
    ) -> Result<crate::engine::CommitReceipt, MemberError> {
        if write.acknowledgement != AcknowledgementLevel::Local {
            return Err(MemberError::AcknowledgementLevelNotImplemented(write.acknowledgement));
        }
        if self.config.topology().owner_of(&write.range) != Some(&self.id) {
            return Err(MemberError::NotOwner { range: write.range });
        }
        let engine = self.engine.as_ref().ok_or(MemberError::EngineClosed)?;
        let receipt = commit_locally(engine, scope, &write)?;
        self.activated = true;
        Ok(receipt)
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
        let engine = self.engine.as_ref().ok_or(MemberError::EngineClosed)?;
        let tenant = scope.tenant().clone();
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
            if requested != tenant {
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
            } else {
                admitted.push((requester, after, limit));
            }
        }
        let mut served = 0;
        for (requester, after, limit) in admitted {
            let batch = build_catch_up_batch(engine, &self.id, &tenant, after, limit)?;
            network.send(&self.id, &requester, batch).map_err(|_| MemberError::TransportFull)?;
            served += 1;
        }
        drop(engine);
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

    /// Applies the catch-up batches addressed to this member for the scope's
    /// tenant.
    ///
    /// Every delivery is merged and applied in ascending source sequence
    /// order, so reordered deliveries replay to the contract outcome.
    /// Duplicate operations resolve through the durable operation ledger;
    /// an operation identity that arrives bound to a different write set
    /// surfaces as the typed [`CommitError::OperationIdReuse`] rejection and
    /// is never silently skipped. A batch the source flagged as below its
    /// servable history either falls back to a full catch-up (when the flag
    /// names this member's own cursor) or fails with the typed
    /// [`MemberError::GapUnservable`].
    ///
    /// # Errors
    ///
    /// Returns [`MemberError::CrossTenant`] when a delivery named another
    /// tenant, [`MemberError::GapUnservable`] for an unservable cursor,
    /// [`MemberError::ApplyFailed`] for a rejected or undecided apply, and
    /// the engine's typed failures otherwise.
    pub(crate) fn apply_deliveries(
        &mut self,
        network: &mut ReplicationNetwork,
        scope: &TenantScope,
    ) -> Result<ApplyReport, MemberError> {
        let tenant = scope.tenant().clone();
        let frames = network.receive(&self.id, FrameSelector::Deliveries);
        let mut batches = Vec::new();
        for frame in frames {
            let ReplicationFrame::CatchUpBatch {
                source,
                tenant: batch_tenant,
                commits,
                through,
                tenant_high_water,
                servable_from,
                gap_below,
            } = frame
            else {
                continue;
            };
            if batch_tenant != tenant {
                self.record_denial(TenantDenial::new(
                    DenialSeam::ApplyDelivery,
                    tenant.clone(),
                    batch_tenant.clone(),
                    source.clone(),
                ));
                return Err(MemberError::CrossTenant { scope: tenant, attempted: batch_tenant });
            }
            if let Some(gap_cursor) = gap_below {
                let advisory_cursor =
                    self.advisory.get(&tenant).and_then(|progress| progress.applied_through);
                if advisory_cursor == Some(gap_cursor) && gap_cursor.get() > 0 {
                    // The source cannot serve below this member's own
                    // cursor: fall back to a full catch-up, never silently
                    // skip the gap.
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
            batches.push(CatchUpDelivery {
                source,
                commits,
                through,
                tenant_high_water,
            });
        }

        let mut report = ApplyReport::default();
        report.batches = batches.len();
        let resumed_from = self.advisory.get(&tenant).and_then(|progress| progress.applied_through);
        let mut merged: BTreeMap<u64, &PublishedCommit> = BTreeMap::new();
        for batch in &batches {
            for commit in &batch.commits {
                if merged.insert(commit.sequence().get(), commit).is_some() {
                    report.duplicates_resolved += 1;
                }
            }
            report.tenant_high_water = max_sequence(report.tenant_high_water, batch.tenant_high_water);
        }
        for (sequence, commit) in merged {
            let sequence = StorageSequence::new(sequence);
            if let Some(resumed) = resumed_from
                && sequence <= resumed
            {
                report.duplicates_resolved += 1;
                continue;
            }
            match self.apply_one(scope, commit)? {
                ApplyStep::Applied => report.applied += 1,
                ApplyStep::Duplicate => report.duplicates_resolved += 1,
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

    /// Applies one served commit through the Engine transaction seam with
    /// its original operation identity.
    fn apply_one(
        &self,
        scope: &TenantScope,
        commit: &PublishedCommit,
    ) -> Result<ApplyStep, MemberError> {
        let engine = self.engine.as_ref().ok_or(MemberError::EngineClosed)?;
        // The durable operation ledger resolves a redelivery before any
        // staging: replaying the same operation identity is a no-op.
        if let DurableResolution::Committed(_) =
            engine.resolve_commit(scope, commit.operation_id().clone()).map_err(MemberError::from)?
        {
            return Ok(ApplyStep::Duplicate);
        }
        let snapshot = engine.read_snapshot(scope).map_err(MemberError::from)?;
        let mut transaction =
            engine.begin(scope, snapshot, commit.operation_id().clone()).map_err(MemberError::from)?;
        for write in commit.writes() {
            match write {
                PublishedWrite::Put { document } => {
                    transaction.put(document.clone(), ExpectedVersion::Any).map_err(MemberError::from)?;
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
            CommitOutcome::Committed(_) => Ok(ApplyStep::Applied),
            CommitOutcome::Rejected(reason) => Err(MemberError::ApplyFailed {
                operation: commit.operation_id().clone(),
                reason: ApplyFailure::Rejected(reason),
            }),
            CommitOutcome::Unknown(unknown) => {
                match engine
                    .resolve_commit(scope, unknown.operation_id().clone())
                    .map_err(MemberError::from)?
                {
                    DurableResolution::Committed(_) => Ok(ApplyStep::Applied),
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

    /// Closes the member's Engine; the durable location keeps its bytes.
    pub(crate) fn close(&mut self) {
        if let Some(engine) = self.engine.take() {
            drop(engine.shutdown());
        }
        self.advisory.clear();
    }

    /// Reopens the member's Engine from its durable location.
    ///
    /// Advisory progress does not survive; the durable cursor does, because
    /// it is derived from the journal on the other side.
    ///
    /// # Errors
    ///
    /// Returns [`MemberError::EngineOpen`] when the location cannot be
    /// reopened and the engine's typed shutdown failure otherwise.
    pub(crate) fn restart(&mut self) -> Result<(), MemberError> {
        if let Some(engine) = self.engine.take() {
            engine
                .shutdown()
                .map_err(|_| MemberError::Engine(EngineError::StorageFailure))?;
        }
        let engine = Engine::open(self.engine_config.clone()).map_err(MemberError::EngineOpen)?;
        self.engine = Some(engine);
        self.advisory.clear();
        self.activated = false;
        self.answered = false;
        Ok(())
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
            let rejection = RejoinRejection::new(
                previous.clone(),
                derived.clone(),
                RejoinReason::CursorRegression,
            );
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
pub(crate) struct ReplicationWrite {
    operation: OperationId,
    range: String,
    acknowledgement: AcknowledgementLevel,
    writes: Vec<LocalWrite>,
}

/// One staged mutation of a local write request.
enum LocalWrite {
    Put(Document),
    Delete(DocumentId),
}

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
                transaction.put(document.clone(), ExpectedVersion::Any).map_err(MemberError::from)?;
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
            match engine.resolve_commit(scope, unknown.operation_id().clone()).map_err(MemberError::from)?
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
fn build_catch_up_batch(
    engine: &Engine,
    source: &MemberId,
    tenant: &TenantId,
    after: Option<StorageSequence>,
    limit: u32,
) -> Result<ReplicationFrame, MemberError> {
    let servable_from = engine.servable_history_start().map_err(MemberError::from)?;
    let gap_below = catch_up_gap(after, servable_from);
    let (commits, through) = if gap_below.is_some() {
        // Serving below the servable bound would silently skip a gap, so
        // the batch is empty and the gap is named instead.
        (Vec::new(), None)
    } else {
        let commits =
            engine.tenant_commits(tenant, after.unwrap_or(StorageSequence::new(0)), limit)
                .map_err(MemberError::from)?;
        let through = commits.last().map(PublishedCommit::sequence);
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
struct CatchUpDelivery {
    source: MemberId,
    commits: Vec<PublishedCommit>,
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

/// The report of one apply pass.
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub(crate) struct ApplyReport {
    applied: usize,
    duplicates_resolved: usize,
    batches: usize,
    through: Option<StorageSequence>,
    tenant_high_water: Option<StorageSequence>,
    caught_up: bool,
}

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
pub(crate) struct TenantDenial {
    seam: DenialSeam,
    scope_tenant: TenantId,
    attempted_tenant: TenantId,
    counterparty: MemberId,
}

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
pub(crate) struct CatchUpFallback {
    tenant: TenantId,
    cursor: StorageSequence,
}

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
pub(crate) struct RejoinRejection {
    previous: Option<StorageSequence>,
    derived: Option<StorageSequence>,
    reason: RejoinReason,
}

impl RejoinRejection {
    /// Records one rejection from its facts.
    fn new(previous: ReplicationCursor, derived: ReplicationCursor, reason: RejoinReason) -> Self {
        Self {
            previous: previous.high_water(),
            derived: derived.high_water(),
            reason,
        }
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
    /// The addressed source member is not seated by this member's view.
    UnknownSource(MemberId),
    /// The named data range is not valid topology input.
    InvalidRange,
    /// This member does not own the named data range's write path.
    NotOwner {
        /// The range the write addressed.
        range: String,
    },
    /// The named acknowledgement level is not evaluated in R-1.
    AcknowledgementLevelNotImplemented(AcknowledgementLevel),
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
