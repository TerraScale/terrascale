//! Lifecycle and archival execution (GAP-F06, TerraBaseV2#75).
//!
//! The typed lifecycle and archival families record *state* — expiry and
//! refresh targets, archival marks and delay policies. This module adds the
//! execution half that state alone cannot provide: the maintenance pass
//! that deletes expired documents, consumes due refresh markers, drives
//! archival policies to their marks, and clears family records whose owner
//! document is gone, plus the visibility read that filters expired
//! documents before any maintenance has run.
//!
//! The execution-owner decision (D1, recorded in
//! `docs/families/lifecycle-execution.md`, **proposed** for parent
//! ratification): the pass body is engine-owned family behavior a host
//! schedules, in the shape of [`Engine::maintain_indexes`](crate::engine::Engine::maintain_indexes)
//! — but split over the family seam's two existing operations, because
//! family code never commits on its own. [`FamilyStore::plan_lifecycle_pass`]
//! is the read (one immutable snapshot, one pass time) and
//! [`FamilyStore::stage_lifecycle_pass`] stages every planned action into
//! the driver's transaction, so the driver owns the cadence *and* the
//! commit. A one-line `Engine::maintain_lifecycle` wrapper that begins and
//! commits the pass itself is recorded as a proposed collision action, not
//! made.
//!
//! V1 semantics mirrored (pinned commit `fa99b925`, citations in the packet
//! record): an elapsed `@expires` deletes the document
//! (`ExpirationStorage.cs:22-42`); a due `@refresh` removes the marker and
//! leaves `@expires` untouched (`RefreshStorage.cs:24-57`); a due archive
//! time marks the document archived (`DataArchivalStorage.cs:22-47`); a
//! record whose document is gone is dropped from the work queue
//! (`DocumentBackgroundWorkStorage.cs:13-24`).
//!
//! What this module does **not** do: it does not commit, does not run on a
//! timer, does not hide *archived* documents from any read (V1 keeps
//! archived documents loadable and filters them only in index and
//! subscription behavior — that integration belongs to the query and
//! streaming owners), does not touch documents outside the pass's tenant
//! scope, and does not drive the tombstone retention watermark — the
//! pass's deletions are ordinary journal delete publications, so the
//! landed GAP-R02 rules govern their evidence.

use crate::engine::Transaction;
use crate::operations::RequestValidationError;
use crate::types::{Cancellation, Deadline, DocumentId, ExpectedVersion, TenantScope};

use super::state::{ArchivalRecord, LifecycleRecord, Records, family_key, now_millis, record_id};
use super::{FamilyError, FamilyKind, FamilyReadContext, FamilyStore, map_begin_error, store};

// ---------------------------------------------------------------------------
// Pass vocabulary
// ---------------------------------------------------------------------------

/// One planned lifecycle maintenance pass over one tenant scope.
///
/// The pass is planned from one immutable snapshot and one pass time, and
/// its actions are staged into the driver's transaction by
/// [`FamilyStore::stage_lifecycle_pass`]. It carries document identities
/// only — never document values.
#[derive(Clone, Debug, PartialEq)]
pub struct LifecyclePass {
    scope: TenantScope,
    deletions: Vec<DocumentId>,
    refreshes: Vec<DocumentId>,
    schedules: Vec<DocumentId>,
    archival: Vec<DocumentId>,
    stale_records: Vec<DocumentId>,
}

impl LifecyclePass {
    /// Creates one pass. Crate-only: a pass is planned by
    /// [`FamilyStore::plan_lifecycle_pass`].
    pub(crate) fn new(scope: TenantScope) -> Self {
        Self {
            scope,
            deletions: Vec::new(),
            refreshes: Vec::new(),
            schedules: Vec::new(),
            archival: Vec::new(),
            stale_records: Vec::new(),
        }
    }

    /// Returns the tenant scope the pass was planned for.
    #[must_use]
    pub fn tenant_scope(&self) -> &TenantScope {
        &self.scope
    }

    /// Returns the documents whose configured expiry has elapsed and that
    /// the pass deletes.
    #[must_use]
    pub fn expired_documents(&self) -> &[DocumentId] {
        &self.deletions
    }

    /// Returns the documents whose refresh marker is due for consumption.
    #[must_use]
    pub fn refresh_due_documents(&self) -> &[DocumentId] {
        &self.refreshes
    }

    /// Returns the documents whose archival policy materializes a schedule
    /// this pass.
    #[must_use]
    pub fn scheduled_archival_documents(&self) -> &[DocumentId] {
        &self.schedules
    }

    /// Returns the documents due to be marked archived this pass.
    #[must_use]
    pub fn archive_due_documents(&self) -> &[DocumentId] {
        &self.archival
    }

    /// Returns the documents whose family records outlived the document
    /// and are cleared this pass.
    #[must_use]
    pub fn stale_records(&self) -> &[DocumentId] {
        &self.stale_records
    }

    /// Reports whether the pass plans no action.
    #[must_use]
    pub fn is_empty(&self) -> bool {
        self.deletions.is_empty()
            && self.refreshes.is_empty()
            && self.schedules.is_empty()
            && self.archival.is_empty()
            && self.stale_records.is_empty()
    }
}

/// What one staged pass actually did, observed before the driver commits.
///
/// Every list carries document identities only. A document appears in
/// [`skipped`](Self::skipped) when the transaction's view no longer matched
/// the plan — its owner was deleted or re-created, it changed after the
/// plan, or its family state changed — so the action is left for the next
/// pass instead of applied to stale facts.
#[derive(Clone, Debug, Default, PartialEq)]
pub struct LifecyclePassStaged {
    deleted: Vec<DocumentId>,
    refreshed: Vec<DocumentId>,
    scheduled: Vec<DocumentId>,
    archived: Vec<DocumentId>,
    cleared_stale: Vec<DocumentId>,
    skipped: Vec<DocumentId>,
}

impl LifecyclePassStaged {
    /// Creates one staged outcome. Crate-only.
    pub(crate) fn new(
        deleted: Vec<DocumentId>,
        refreshed: Vec<DocumentId>,
        scheduled: Vec<DocumentId>,
        archived: Vec<DocumentId>,
        cleared_stale: Vec<DocumentId>,
        skipped: Vec<DocumentId>,
    ) -> Self {
        Self { deleted, refreshed, scheduled, archived, cleared_stale, skipped }
    }

    /// Returns the documents whose deletion was staged.
    #[must_use]
    pub fn deleted(&self) -> &[DocumentId] {
        &self.deleted
    }

    /// Returns the documents whose refresh marker consumption was staged.
    #[must_use]
    pub fn refreshed(&self) -> &[DocumentId] {
        &self.refreshed
    }

    /// Returns the documents whose archival schedule materialization was
    /// staged.
    #[must_use]
    pub fn scheduled(&self) -> &[DocumentId] {
        &self.scheduled
    }

    /// Returns the documents whose archival mark was staged.
    #[must_use]
    pub fn archived(&self) -> &[DocumentId] {
        &self.archived
    }

    /// Returns the documents whose stale family records were cleared.
    #[must_use]
    pub fn cleared_stale(&self) -> &[DocumentId] {
        &self.cleared_stale
    }

    /// Returns the documents the pass skipped because the observed state
    /// no longer matched the plan.
    #[must_use]
    pub fn skipped(&self) -> &[DocumentId] {
        &self.skipped
    }
}

// ---------------------------------------------------------------------------
// Visibility vocabulary
// ---------------------------------------------------------------------------

/// A typed batch visibility read of documents through the lifecycle lens.
#[derive(Clone, Debug, PartialEq)]
pub struct VisibilityRead {
    documents: Vec<DocumentId>,
    deadline: Deadline,
    cancellation: Cancellation,
}

impl VisibilityRead {
    /// Creates a validated visibility read.
    ///
    /// # Errors
    ///
    /// Returns [`RequestValidationError::InvalidFamilyPayload`] for an
    /// empty document list, [`RequestValidationError::InvalidDeadline`] for
    /// an elapsed deadline, and
    /// [`RequestValidationError::InvalidCancellation`] for an already
    /// cancelled signal.
    pub fn try_new(
        documents: Vec<DocumentId>,
        deadline: Deadline,
        cancellation: Cancellation,
    ) -> Result<Self, RequestValidationError> {
        if documents.is_empty() {
            return Err(RequestValidationError::InvalidFamilyPayload);
        }
        if deadline.is_expired() {
            return Err(RequestValidationError::InvalidDeadline);
        }
        if cancellation.is_cancelled() {
            return Err(RequestValidationError::InvalidCancellation);
        }
        Ok(Self { documents, deadline, cancellation })
    }

    /// Returns the requested document identities, in request order.
    #[must_use]
    pub fn documents(&self) -> &[DocumentId] {
        &self.documents
    }

    /// Returns the request deadline.
    #[must_use]
    pub fn deadline(&self) -> Deadline {
        self.deadline
    }

    /// Returns a clone of the cancellation signal.
    #[must_use]
    pub fn cancellation(&self) -> Cancellation {
        self.cancellation.clone()
    }
}

/// One document's visibility through the lifecycle lens.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum VisibilityState {
    /// The document exists and its configured expiry has not elapsed.
    Visible,
    /// The document exists but its configured expiry has elapsed; it is not
    /// visible even before maintenance deletes it.
    Expired,
    /// No such document exists in this snapshot.
    Missing,
}

/// One document's visibility entry.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct DocumentVisibility {
    document: DocumentId,
    state: VisibilityState,
}

impl DocumentVisibility {
    /// Creates one entry. Crate-only.
    pub(crate) fn new(document: DocumentId, state: VisibilityState) -> Self {
        Self { document, state }
    }

    /// Returns the document identity.
    #[must_use]
    pub fn document(&self) -> &DocumentId {
        &self.document
    }

    /// Returns the visibility state.
    #[must_use]
    pub fn state(&self) -> VisibilityState {
        self.state
    }
}

/// The result of one visibility read: one entry per requested document, in
/// request order, each with exactly one state.
#[derive(Clone, Debug, PartialEq)]
pub struct VisibilityReport {
    entries: Vec<DocumentVisibility>,
}

impl VisibilityReport {
    /// Creates one report. Crate-only.
    pub(crate) fn new(entries: Vec<DocumentVisibility>) -> Self {
        Self { entries }
    }

    /// Returns every entry, in request order.
    #[must_use]
    pub fn entries(&self) -> &[DocumentVisibility] {
        &self.entries
    }
}

// ---------------------------------------------------------------------------
// Planning helpers
// ---------------------------------------------------------------------------

/// Returns the owner document of one reserved lifecycle or archival record
/// identifier.
///
/// The owner is everything after the reserved kind prefix — document
/// identifiers may themselves contain `/`, so the owner is never split. An
/// identifier that does not rebuild to itself through [`record_id`] cannot
/// have been minted by the family write path and is a reserved-state
/// collision.
fn owner_of(id: &DocumentId, kind: &str) -> Result<Option<DocumentId>, FamilyError> {
    let prefix = format!("family:{kind}/");
    let Some(owner) = id.as_str().strip_prefix(&prefix) else {
        return Ok(None);
    };
    if owner.is_empty() {
        return Ok(None);
    }
    let rebuilt = record_id(kind, &[owner])?;
    if rebuilt != *id {
        return Ok(None);
    }
    DocumentId::try_new(owner).map(Some).map_err(|_| FamilyError::InvalidRequest)
}

/// Maps snapshot enumeration failures to their family cases, mirroring
/// [`FamilyStore::snapshot_family_records`](super::FamilyStore::snapshot_family_records).
fn map_snapshot_error(error: &crate::engine::EngineError) -> FamilyError {
    match error {
        crate::engine::EngineError::SnapshotEngineMismatch
        | crate::engine::EngineError::ScopeMismatch => FamilyError::SnapshotEngineMismatch,
        crate::engine::EngineError::SnapshotTenantMismatch => FamilyError::TenantMismatch,
        crate::engine::EngineError::Closed(_) => FamilyError::EngineClosed,
        _ => FamilyError::StorageFailure,
    }
}

/// Decodes one lifecycle record value, failing closed when the value is
/// not an object.
///
/// The shared [`LifecycleRecord::decode`](super::state::LifecycleRecord::decode)
/// reads every field off a non-object value as absent, so a squatter's
/// string, number, or `null` at a reserved lifecycle identifier would
/// decode as "no lifecycle state" and read as benign. Repair loop 1 made
/// the pass fail closed on that shape instead — the same contract
/// [`ArchivalRecord::decode`] already gives (its required `archived`
/// boolean rejects every non-object value). An object still decodes
/// through the shared decoder, so absent fields mean absent state.
fn decode_lifecycle_record(
    value: &crate::types::DocumentValue,
) -> Result<LifecycleRecord, FamilyError> {
    if !matches!(value, crate::types::DocumentValue::Object(_)) {
        return Err(FamilyError::ReservedStateInvalid);
    }
    LifecycleRecord::decode(value)
}

// ---------------------------------------------------------------------------
// Execution
// ---------------------------------------------------------------------------

impl<'engine> FamilyStore<'engine> {
    /// Plans one lifecycle maintenance pass for one tenant scope.
    ///
    /// The plan observes one immutable snapshot and one pass time,
    /// enumerates the tenant's family records through the landed
    /// [`FamilyStore::snapshot_family_records`] materialization (the same
    /// W3-F seam backup consumes), and keeps only lifecycle and archival
    /// records; owner presence and version are point reads. An elapsed
    /// expiry plans a deletion, a due refresh marker plans its
    /// consumption, an archival policy without a schedule plans the
    /// schedule's materialization, a due archival schedule plans the
    /// archival mark, and a record whose owner document is gone plans the
    /// record's cleanup. Expiry outranks refresh and archival on the same
    /// document: the document is going away, so the other actions would
    /// operate on a corpse (V1's deletion makes the queued work moot the
    /// same way).
    ///
    /// # Errors
    ///
    /// Returns [`FamilyError::TenantMismatch`],
    /// [`FamilyError::SnapshotEngineMismatch`],
    /// [`FamilyError::EngineClosed`], or [`FamilyError::StorageFailure`]
    /// for scope, snapshot, and storage failures, and
    /// [`FamilyError::ReservedStateInvalid`] when a reserved-collection
    /// document does not parse as family state or holds a malformed value
    /// — the recorded W3-F collision limit, which fails the pass closed
    /// rather than skipping state it cannot name.
    pub fn plan_lifecycle_pass(&self, scope: &TenantScope) -> Result<LifecyclePass, FamilyError> {
        let snapshot =
            self.engine.read_snapshot(scope).map_err(|error| map_snapshot_error(&error))?;
        let operation = store::internal_read_operation()?;
        let mut transaction = self
            .engine
            .begin(scope, snapshot.clone(), operation)
            .map_err(|error| map_begin_error(&error))?;
        let mut records = Records::new(&mut transaction);
        let mut pass = LifecyclePass::new(scope.clone());
        let now = now_millis();

        // Repair loop 1: enumerate the tenant's family records through the
        // W3-F materialization seam instead of windowed paging. The
        // storage seam's windowed read resumes strictly after the FIRST
        // canonical key bearing the cursor identifier
        // (src/storage/root.rs, `tenant_documents_from`), so a tenant that
        // reuses one document identifier across collections cycled the
        // old paging loop forever; the materialization pages nothing and
        // observes every record exactly once.
        let mut found: Vec<(FamilyKind, DocumentId, crate::types::DocumentValue)> = Vec::new();
        for record in self.snapshot_family_records(scope, &snapshot)? {
            match record.kind() {
                FamilyKind::ExpirationRefresh | FamilyKind::Archival => {
                    found.push((record.kind(), record.id().clone(), record.value().clone()));
                }
                _ => {}
            }
        }

        for (kind, id, value) in found {
            match kind {
                FamilyKind::ExpirationRefresh => {
                    let Some(owner) = owner_of(&id, family_key(FamilyKind::ExpirationRefresh))?
                    else {
                        return Err(FamilyError::ReservedStateInvalid);
                    };
                    let record = decode_lifecycle_record(&value)?;
                    if records.user_document(&owner)?.is_none() {
                        // V1 drops queue entries whose document is gone
                        // (DocumentBackgroundWorkStorage.cs:13-24).
                        push_unique(&mut pass.stale_records, owner);
                        continue;
                    }
                    if record.is_expired(now) {
                        push_unique(&mut pass.deletions, owner);
                    } else if record.refresh_at.is_some_and(|at| at <= now) {
                        push_unique(&mut pass.refreshes, owner);
                    }
                }
                FamilyKind::Archival => {
                    let Some(owner) = owner_of(&id, family_key(FamilyKind::Archival))? else {
                        return Err(FamilyError::ReservedStateInvalid);
                    };
                    let record = ArchivalRecord::decode(&value)?;
                    if records.user_document(&owner)?.is_none() {
                        push_unique(&mut pass.stale_records, owner);
                        continue;
                    }
                    if record.archived {
                        continue;
                    }
                    if record.at.is_some_and(|at| now >= at) {
                        push_unique(&mut pass.archival, owner);
                    } else if record.at.is_none() && record.after_ms.is_some() {
                        push_unique(&mut pass.schedules, owner);
                    }
                }
                _ => {}
            }
        }
        Ok(pass)
    }

    /// Stages one planned lifecycle pass into the caller's transaction.
    ///
    /// Every action re-verifies its facts inside the transaction before it
    /// stages anything: a deletion requires the owner present at the
    /// planned expiry with the version the transaction observes (staged
    /// with [`ExpectedVersion::Exact`], so a writer that lands between this
    /// transaction's snapshot and its commit rejects the whole commit and
    /// the next pass re-plans); refresh consumption and archival actions
    /// require their recorded state unchanged; stale cleanup requires the
    /// owner still gone. An action whose facts no longer hold is skipped
    /// into [`LifecyclePassStaged::skipped`], never applied to stale
    /// state. The driver commits; nothing here is durable before that.
    ///
    /// A deletion is one journaled delete publication — the same seam a
    /// caller's delete rides — so the landed GAP-R02 tombstone retention
    /// rules govern its deletion evidence, and the pass records the
    /// lifecycle family tombstone for the deleted document so a stale
    /// writer cannot resurrect lifecycle state onto the fresh version
    /// space.
    ///
    /// # Errors
    ///
    /// Returns [`FamilyError::TransactionEngineMismatch`] or
    /// [`FamilyError::TenantMismatch`] when the transaction is not live
    /// under the pass's scope, [`FamilyError::StorageFailure`] for storage
    /// failures, and [`FamilyError::ReservedStateInvalid`] when a reserved
    /// record cannot be read as family state.
    pub fn stage_lifecycle_pass(
        &self,
        transaction: &mut Transaction<'engine>,
        pass: &LifecyclePass,
    ) -> Result<LifecyclePassStaged, FamilyError> {
        self.bind_transaction(transaction, pass.tenant_scope())?;
        let mut skipped = Vec::new();
        // Deletions first: expiry outranks the other actions, and each
        // deletion is staged as one unit — document delete, lifecycle
        // record clear, family tombstone — inside the driver's single
        // transaction, so no intermediate state is observable.
        let deleted = stage_expiry_deletions(transaction, &pass.deletions, &mut skipped)?;
        // Refresh consumption: the due marker is removed and the expiry is
        // left untouched (V1 RefreshStorage.cs:31-42).
        let refreshed = stage_refresh_consumption(transaction, &pass.refreshes, &mut skipped)?;
        // Archival schedule materialization (decision D3 in the packet
        // record) and archival marks (V1 DataArchivalStorage.cs:35-44).
        let scheduled = stage_archival_schedules(transaction, &pass.schedules, &mut skipped)?;
        let archived = stage_archival_marks(transaction, &pass.archival, &mut skipped)?;
        // Stale cleanup: the owner is still gone, so the family records
        // that outlived it are cleared.
        let cleared_stale = stage_stale_cleanup(transaction, &pass.stale_records, &mut skipped)?;
        Ok(LifecyclePassStaged::new(
            deleted,
            refreshed,
            scheduled,
            archived,
            cleared_stale,
            skipped,
        ))
    }

    /// Reads one batch of documents' visibility through the lifecycle lens.
    ///
    /// One read transaction observes every requested document and its
    /// lifecycle record at one snapshot, so each entry carries exactly one
    /// state: a present document whose configured expiry has elapsed is
    /// [`VisibilityState::Expired`] — not visible — *before* any
    /// maintenance deletes it; there is no partial-expiry state in which
    /// the same snapshot shows the document both alive and doomed. The
    /// transaction is never committed.
    ///
    /// # Errors
    ///
    /// Returns [`FamilyError::TenantMismatch`] or
    /// [`FamilyError::SnapshotEngineMismatch`] for an invalid context,
    /// [`FamilyError::DeadlineExceeded`] or [`FamilyError::Cancelled`]
    /// before the read runs, and
    /// [`FamilyError::ReservedStateInvalid`] when a reserved record cannot
    /// be read as family state.
    #[allow(clippy::needless_pass_by_value)] // the seam contract passes the context by value
    pub fn read_document_visibility(
        &self,
        context: FamilyReadContext,
        request: VisibilityRead,
    ) -> Result<VisibilityReport, FamilyError> {
        FamilyReadContext::validate(context.tenant(), context.snapshot())?;
        if request.deadline.is_expired() {
            return Err(FamilyError::DeadlineExceeded);
        }
        if request.cancellation.is_cancelled() {
            return Err(FamilyError::Cancelled);
        }
        let operation = store::internal_read_operation()?;
        let mut transaction = self
            .engine
            .begin(context.tenant(), context.snapshot().clone(), operation)
            .map_err(|error| map_begin_error(&error))?;
        let mut records = Records::new(&mut transaction);
        let now = now_millis();
        let mut entries = Vec::new();
        for document in &request.documents {
            let state = if records.user_document(document)?.is_none() {
                VisibilityState::Missing
            } else {
                let id = record_id("lifecycle", &[document.as_str()])?;
                let expired = match records.read(&id)? {
                    Some(value) => decode_lifecycle_record(&value)?.is_expired(now),
                    None => false,
                };
                if expired { VisibilityState::Expired } else { VisibilityState::Visible }
            };
            entries.push(DocumentVisibility::new(document.clone(), state));
        }
        Ok(VisibilityReport::new(entries))
    }
}

/// Stages the planned expiry deletions, re-verified inside the
/// transaction. Each staged unit is the document delete (exact expected
/// version), the lifecycle record clear, and the lifecycle family
/// tombstone.
fn stage_expiry_deletions(
    transaction: &mut Transaction<'_>,
    documents: &[DocumentId],
    skipped: &mut Vec<DocumentId>,
) -> Result<Vec<DocumentId>, FamilyError> {
    let now = now_millis();
    let mut deleted = Vec::new();
    for document in documents {
        let id = record_id("lifecycle", &[document.as_str()])?;
        let Some(record_value) = Records::new(transaction).read(&id)? else {
            skipped.push(document.clone());
            continue;
        };
        let record = decode_lifecycle_record(&record_value)?;
        let Some((_, version, _)) = Records::new(transaction).user_document(document)? else {
            skipped.push(document.clone());
            continue;
        };
        // V1 re-verifies the expiry inside the deleting command
        // (ExpirationStorage.cs:29-35); the transaction's view is the
        // authority, not the plan.
        if !record.is_expired(now) {
            skipped.push(document.clone());
            continue;
        }
        transaction
            .delete(document.clone(), ExpectedVersion::Exact(version))
            .map_err(|_| FamilyError::StorageFailure)?;
        let mut records = Records::new(transaction);
        records.clear(&id)?;
        // Record the lifecycle family tombstone with the same encoding the
        // public `TombstoneWrite::Record` stage produces, so the landed
        // guard order fences stale lifecycle writers.
        let tombstone_id = record_id("tombstones", &[document.as_str()])?;
        let mut tombstone = match records.read(&tombstone_id)? {
            Some(value) => super::state::TombstoneRecord::decode(&value)?,
            None => super::state::TombstoneRecord::default(),
        };
        tombstone
            .entries
            .insert(family_key(FamilyKind::ExpirationRefresh).to_owned(), (version, now_millis()));
        records.write(tombstone_id, tombstone.encode())?;
        deleted.push(document.clone());
    }
    Ok(deleted)
}

/// Stages the planned refresh consumptions: the due marker is removed and
/// the expiry is left untouched (V1 RefreshStorage.cs:31-42).
fn stage_refresh_consumption(
    transaction: &mut Transaction<'_>,
    documents: &[DocumentId],
    skipped: &mut Vec<DocumentId>,
) -> Result<Vec<DocumentId>, FamilyError> {
    let now = now_millis();
    let mut refreshed = Vec::new();
    for document in documents {
        let id = record_id("lifecycle", &[document.as_str()])?;
        let mut records = Records::new(transaction);
        let Some(record_value) = records.read(&id)? else {
            skipped.push(document.clone());
            continue;
        };
        let mut record = decode_lifecycle_record(&record_value)?;
        if records.user_document(document)?.is_none() || record.refresh_at.is_none_or(|at| at > now)
        {
            skipped.push(document.clone());
            continue;
        }
        record.refresh_at = None;
        write_record(
            &mut records,
            id,
            record.expires_at.is_none() && record.refresh_at.is_none(),
            record.encode(),
        )?;
        refreshed.push(document.clone());
    }
    Ok(refreshed)
}

/// Stages the planned archival schedule materializations: a policy without
/// a schedule gets `at = now + after_ms`, recorded durably in the archival
/// record (decision D3 in the packet record).
fn stage_archival_schedules(
    transaction: &mut Transaction<'_>,
    documents: &[DocumentId],
    skipped: &mut Vec<DocumentId>,
) -> Result<Vec<DocumentId>, FamilyError> {
    let now = now_millis();
    let mut scheduled = Vec::new();
    for document in documents {
        let id = record_id("archival", &[document.as_str()])?;
        let mut records = Records::new(transaction);
        let Some(record_value) = records.read(&id)? else {
            skipped.push(document.clone());
            continue;
        };
        let mut record = ArchivalRecord::decode(&record_value)?;
        if records.user_document(document)?.is_none()
            || record.archived
            || record.at.is_some()
            || record.after_ms.is_none()
        {
            skipped.push(document.clone());
            continue;
        }
        record.at = Some(now.saturating_add(record.after_ms.unwrap_or(0)));
        records.write(id, record.encode())?;
        scheduled.push(document.clone());
    }
    Ok(scheduled)
}

/// Stages the planned archival marks: a due schedule archives the document
/// (V1 DataArchivalStorage.cs:35-44 sets the archived state at the due
/// time).
fn stage_archival_marks(
    transaction: &mut Transaction<'_>,
    documents: &[DocumentId],
    skipped: &mut Vec<DocumentId>,
) -> Result<Vec<DocumentId>, FamilyError> {
    let now = now_millis();
    let mut archived = Vec::new();
    for document in documents {
        let id = record_id("archival", &[document.as_str()])?;
        let mut records = Records::new(transaction);
        let Some(record_value) = records.read(&id)? else {
            skipped.push(document.clone());
            continue;
        };
        let mut record = ArchivalRecord::decode(&record_value)?;
        if records.user_document(document)?.is_none()
            || record.archived
            || record.at.is_none_or(|at| now < at)
        {
            skipped.push(document.clone());
            continue;
        }
        record.archived = true;
        records.write(id, record.encode())?;
        archived.push(document.clone());
    }
    Ok(archived)
}

/// Stages the planned stale cleanups: the owner is still gone, so the
/// lifecycle and archival records that outlived it are cleared.
fn stage_stale_cleanup(
    transaction: &mut Transaction<'_>,
    documents: &[DocumentId],
    skipped: &mut Vec<DocumentId>,
) -> Result<Vec<DocumentId>, FamilyError> {
    let mut cleared = Vec::new();
    for document in documents {
        let mut records = Records::new(transaction);
        if records.user_document(document)?.is_some() {
            skipped.push(document.clone());
            continue;
        }
        let lifecycle_id = record_id("lifecycle", &[document.as_str()])?;
        if records.read(&lifecycle_id)?.is_some() {
            records.clear(&lifecycle_id)?;
        }
        let archival_id = record_id("archival", &[document.as_str()])?;
        if records.read(&archival_id)?.is_some() {
            records.clear(&archival_id)?;
        }
        cleared.push(document.clone());
    }
    Ok(cleared)
}

/// Writes one record value, or clears the record when it holds no state —
/// the same shape the landed stage functions use.
fn write_record(
    records: &mut Records<'_, '_>,
    id: crate::types::DocumentId,
    empty: bool,
    encode: crate::types::DocumentValue,
) -> Result<(), FamilyError> {
    if empty { records.clear(&id) } else { records.write(id, encode) }
}

/// Pushes one owner unless the list already carries it.
///
/// Canonical key order places every `family:archival/*` record before
/// every `family:lifecycle/*` record, so one owner with both record kinds
/// reaches a plan list through two non-adjacent visits, and the plan's
/// record enumeration is exactly-once but not owner-grouped. The full
/// membership check keeps every plan list a set of owners in first-visit
/// order.
fn push_unique(list: &mut Vec<DocumentId>, owner: DocumentId) {
    if !list.contains(&owner) {
        list.push(owner);
    }
}

#[cfg(test)]
mod tests {
    //! Focused GAP-F06 tests: the red suite first pinned the contract
    //! against a naive baseline (an empty pass and no visibility
    //! filtering); every behavioral test below discriminates that baseline.

    use std::path::{Path, PathBuf};
    use std::sync::mpsc;
    use std::time::Duration;

    use super::super::payloads::{ArchivalWrite, LifecycleWrite};
    use super::super::state::{RESERVED_COLLECTION, Records, now_millis, record_id};
    use super::super::{
        ArchivalCommand, ArchivalReadRequest, FamilyError, FamilyReadContext, FamilyReadRequest,
        FamilyReadResult, FamilyWriteRequest, LifecycleCommand, LifecycleRead,
    };
    use super::{VisibilityRead, VisibilityState};
    use crate::backup::{BackupPlan, RestorePlan, run_backup};
    use crate::engine::{CommitOutcome, Engine, EngineConfig, Transaction};
    use crate::operations::RequestValidationError;
    use crate::types::{
        Cancellation, CollectionName, Deadline, Document, DocumentId, DocumentValue,
        ExpectedVersion, LocalDurability, OperationId, TenantId, TenantScope,
    };

    // ------------------------------------------------------------------
    // Fixtures (mirroring the W3-F in-crate suite)
    // ------------------------------------------------------------------

    fn location(tag: &str) -> PathBuf {
        let unique = format!(
            "tbv2-gapf06-{tag}-{}-{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .unwrap_or_default()
                .as_nanos()
        );
        std::env::temp_dir().join(unique)
    }

    fn fresh_dir(tag: &str) -> PathBuf {
        let dir = location(tag);
        std::fs::remove_dir_all(&dir).ok();
        dir
    }

    fn open_engine(tag: &str) -> (PathBuf, Engine) {
        let dir = fresh_dir(tag);
        let engine =
            Engine::open(EngineConfig::durable(&dir).expect("durable location")).expect("engine");
        (dir, engine)
    }

    fn tenant(name: &str) -> TenantId {
        TenantId::try_new(name).expect("tenant")
    }

    fn document_id(name: &str) -> DocumentId {
        DocumentId::try_new(name).expect("identifier")
    }

    fn operation(name: &str) -> OperationId {
        OperationId::try_new(name).expect("identifier")
    }

    fn scope(engine: &Engine, tenant_name: &str) -> TenantScope {
        TenantScope::from_parts(tenant(tenant_name), engine.identity())
    }

    fn deadline() -> Deadline {
        Deadline::after(Duration::from_secs(45)).expect("deadline")
    }

    fn cancellation() -> Cancellation {
        Cancellation::new()
    }

    fn user_value(fields: &[(&str, &str)]) -> DocumentValue {
        DocumentValue::Object(
            fields
                .iter()
                .map(|(key, value)| ((*key).to_owned(), DocumentValue::String((*value).to_owned())))
                .collect(),
        )
    }

    /// Commits one transaction staging documents and family writes.
    fn commit<'engine>(
        engine: &'engine Engine,
        tenant_name: &str,
        operation_name: &str,
        stage: impl FnOnce(
            &super::super::FamilyStore<'engine>,
            &mut Transaction<'engine>,
            &TenantScope,
            &OperationId,
        ),
    ) -> CommitOutcome {
        let tenant_scope = scope(engine, tenant_name);
        let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
        let identity = operation(operation_name);
        let store = engine.families();
        let mut transaction =
            engine.begin(&tenant_scope, snapshot, identity.clone()).expect("begins");
        stage(&store, &mut transaction, &tenant_scope, &identity);
        let outcome = transaction.commit(LocalDurability::Synced);
        assert!(
            matches!(outcome, crate::engine::CommitOutcome::Committed(_)),
            "fixture transaction {operation_name} must commit, got {outcome:?}"
        );
        outcome
    }

    fn put_user_document(
        engine: &Engine,
        tenant_name: &str,
        operation_name: &str,
        id: &str,
        value: DocumentValue,
    ) {
        commit(engine, tenant_name, operation_name, |_, transaction, _, _| {
            let document = Document::new(
                document_id(id),
                CollectionName::try_new("users").expect("collection"),
                value,
            );
            transaction.put(document, ExpectedVersion::Any).expect("puts");
        });
    }

    fn stage_expiration<'a>(
        store: &super::super::FamilyStore<'a>,
        transaction: &mut Transaction<'a>,
        tenant_scope: &TenantScope,
        identity: &OperationId,
        id: &str,
        at: Option<i64>,
    ) {
        store
            .stage(
                transaction,
                FamilyWriteRequest::lifecycle_update(
                    LifecycleWrite::try_new(
                        tenant_scope.clone(),
                        document_id(id),
                        LifecycleCommand::SetExpiration { at },
                        ExpectedVersion::Any,
                        identity.clone(),
                        deadline(),
                        cancellation(),
                    )
                    .expect("valid lifecycle write"),
                ),
            )
            .expect("stages the expiry");
    }

    fn stage_refresh<'a>(
        store: &super::super::FamilyStore<'a>,
        transaction: &mut Transaction<'a>,
        tenant_scope: &TenantScope,
        identity: &OperationId,
        id: &str,
        at: Option<i64>,
    ) {
        store
            .stage(
                transaction,
                FamilyWriteRequest::lifecycle_update(
                    LifecycleWrite::try_new(
                        tenant_scope.clone(),
                        document_id(id),
                        LifecycleCommand::SetRefresh { at },
                        ExpectedVersion::Any,
                        identity.clone(),
                        deadline(),
                        cancellation(),
                    )
                    .expect("valid lifecycle write"),
                ),
            )
            .expect("stages the refresh");
    }

    fn stage_archival_policy<'a>(
        store: &super::super::FamilyStore<'a>,
        transaction: &mut Transaction<'a>,
        tenant_scope: &TenantScope,
        identity: &OperationId,
        id: &str,
        after_ms: i64,
    ) {
        store
            .stage(
                transaction,
                FamilyWriteRequest::archival_update(
                    ArchivalWrite::try_new(
                        tenant_scope.clone(),
                        document_id(id),
                        ArchivalCommand::SetPolicy { after_ms },
                        identity.clone(),
                        deadline(),
                        cancellation(),
                    )
                    .expect("valid archival write"),
                ),
            )
            .expect("stages the archival policy");
    }

    fn read_family(
        engine: &Engine,
        tenant_name: &str,
        request: FamilyReadRequest,
    ) -> Result<FamilyReadResult, FamilyError> {
        let tenant_scope = scope(engine, tenant_name);
        let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
        engine.families().read(FamilyReadContext::new(tenant_scope, snapshot), request)
    }

    fn lifecycle_state(engine: &Engine, id: &str) -> super::super::LifecycleResult {
        match read_family(
            engine,
            "tenant-a",
            FamilyReadRequest::lifecycle(
                LifecycleRead::try_new(document_id(id), deadline(), cancellation())
                    .expect("valid lifecycle read"),
            ),
        )
        .expect("reads lifecycle state")
        {
            FamilyReadResult::Lifecycle(result) => result,
            other => panic!("expected a lifecycle result, got {other:?}"),
        }
    }

    fn archival_state(engine: &Engine, id: &str) -> Option<super::super::ArchivalState> {
        match read_family(
            engine,
            "tenant-a",
            FamilyReadRequest::archival(
                ArchivalReadRequest::try_new(document_id(id), deadline(), cancellation())
                    .expect("valid archival read"),
            ),
        )
        .expect("reads archival state")
        {
            FamilyReadResult::Archival(result) => result.state().cloned(),
            other => panic!("expected an archival result, got {other:?}"),
        }
    }

    /// Stages one planned pass into a fresh transaction and commits it —
    /// unless the pass staged nothing, in which case the transaction is
    /// dropped: the Engine rejects an empty write set, and a pass with no
    /// action publishes nothing.
    fn stage_pass(
        engine: &Engine,
        tenant_name: &str,
        operation_name: &str,
        pass: &super::LifecyclePass,
    ) -> super::LifecyclePassStaged {
        let tenant_scope = scope(engine, tenant_name);
        let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
        let identity = operation(operation_name);
        let store = engine.families();
        let mut transaction = engine.begin(&tenant_scope, snapshot, identity).expect("begins");
        let staged = store.stage_lifecycle_pass(&mut transaction, pass).expect("stages the pass");
        let acted = !staged.deleted().is_empty()
            || !staged.refreshed().is_empty()
            || !staged.scheduled().is_empty()
            || !staged.archived().is_empty()
            || !staged.cleared_stale().is_empty();
        if acted {
            let outcome = transaction.commit(LocalDurability::Synced);
            assert!(
                matches!(outcome, crate::engine::CommitOutcome::Committed(_)),
                "the pass transaction must commit, got {outcome:?}"
            );
        }
        staged
    }

    /// Runs one full pass: plan, stage into a fresh transaction, commit.
    fn run_pass(
        engine: &Engine,
        tenant_name: &str,
        operation_name: &str,
    ) -> super::LifecyclePassStaged {
        let pass = engine
            .families()
            .plan_lifecycle_pass(&scope(engine, tenant_name))
            .expect("plans the pass");
        stage_pass(engine, tenant_name, operation_name, &pass)
    }

    fn visibility(engine: &Engine, tenant_name: &str, ids: &[&str]) -> Vec<VisibilityState> {
        let tenant_scope = scope(engine, tenant_name);
        let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
        let request = VisibilityRead::try_new(
            ids.iter().map(|id| document_id(id)).collect(),
            deadline(),
            cancellation(),
        )
        .expect("valid visibility read");
        let report = engine
            .families()
            .read_document_visibility(FamilyReadContext::new(tenant_scope, snapshot), request)
            .expect("reads visibility");
        report.entries().iter().map(super::DocumentVisibility::state).collect()
    }

    fn document_present(engine: &Engine, tenant_name: &str, id: &str) -> bool {
        let tenant_scope = scope(engine, tenant_name);
        let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
        let transaction =
            engine.begin(&tenant_scope, snapshot, operation("presence-probe")).expect("begins");
        transaction.read(&document_id(id)).expect("reads").is_some()
    }

    /// Reads one family record's raw presence through a read transaction.
    fn record_present(engine: &Engine, kind: &str, owner: &str) -> bool {
        let tenant_scope = scope(engine, "tenant-a");
        let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
        let mut transaction =
            engine.begin(&tenant_scope, snapshot, operation("record-probe")).expect("begins");
        let mut records = Records::new(&mut transaction);
        let id = record_id(kind, &[owner]).expect("record id");
        records.read(&id).expect("reads the record").is_some()
    }

    // ------------------------------------------------------------------
    // Expiry deletion execution
    // ------------------------------------------------------------------

    #[test]
    fn expired_documents_are_deleted_by_the_pass_and_record_evidence() {
        let (_dir, engine) = open_engine("expiry-delete");
        let now = now_millis();
        put_user_document(
            &engine,
            "tenant-a",
            "put-expired",
            "doc-expired",
            user_value(&[("n", "v")]),
        );
        put_user_document(&engine, "tenant-a", "put-live", "doc-live", user_value(&[("n", "v")]));
        commit(&engine, "tenant-a", "expire-1", |store, transaction, ts, id| {
            stage_expiration(store, transaction, ts, id, "doc-expired", Some(now - 1));
            stage_expiration(store, transaction, ts, id, "doc-live", Some(now + 3_600_000));
        });

        let pass =
            engine.families().plan_lifecycle_pass(&scope(&engine, "tenant-a")).expect("plans");
        assert_eq!(pass.expired_documents(), [document_id("doc-expired")]);
        assert!(pass.refresh_due_documents().is_empty());

        let staged = run_pass(&engine, "tenant-a", "pass-1");
        assert_eq!(staged.deleted(), [document_id("doc-expired")]);

        assert!(!document_present(&engine, "tenant-a", "doc-expired"));
        assert!(
            document_present(&engine, "tenant-a", "doc-live"),
            "only the expired document dies"
        );
        let state = lifecycle_state(&engine, "doc-expired");
        assert!(!state.present(), "the deleted document is not resurrected");
        assert!(
            !record_present(&engine, "lifecycle", "doc-expired"),
            "the lifecycle record is cleared with the deletion"
        );

        // The deletion records family evidence: a tombstone for the
        // lifecycle family fences stale writers.
        let tombstones = match read_family(
            &engine,
            "tenant-a",
            FamilyReadRequest::tombstones(
                super::super::TombstoneRead::try_new(
                    document_id("doc-expired"),
                    deadline(),
                    cancellation(),
                )
                .expect("valid tombstone read"),
            ),
        )
        .expect("reads tombstones")
        {
            FamilyReadResult::Tombstones(result) => result,
            other => panic!("expected a tombstone result, got {other:?}"),
        };
        assert!(
            tombstones
                .entries()
                .iter()
                .any(|entry| entry.family() == crate::families::FamilyKind::ExpirationRefresh),
            "the pass records the lifecycle family tombstone"
        );
        engine.shutdown().expect("shuts down");
    }

    #[test]
    fn pass_deletion_skips_a_document_that_changed_after_the_plan() {
        let (_dir, engine) = open_engine("expiry-race");
        let now = now_millis();
        put_user_document(&engine, "tenant-a", "put-1", "doc-1", user_value(&[("n", "v1")]));
        commit(&engine, "tenant-a", "expire-1", |store, transaction, ts, id| {
            stage_expiration(store, transaction, ts, id, "doc-1", Some(now - 1));
        });

        let pass = engine
            .families()
            .plan_lifecycle_pass(&scope(&engine, "tenant-a"))
            .expect("plans against the elapsed expiry");
        // A writer lands between the plan and the stage: the expiry is
        // cleared on a fresh document version.
        commit(&engine, "tenant-a", "concurrent-put", |store, transaction, ts, id| {
            stage_expiration(store, transaction, ts, id, "doc-1", None);
        });

        let staged = stage_pass(&engine, "tenant-a", "pass-1", &pass);
        assert!(
            staged.skipped().contains(&document_id("doc-1")),
            "a document whose expiry was cleared after the plan is skipped, not deleted"
        );
        assert!(staged.deleted().is_empty());
        assert!(document_present(&engine, "tenant-a", "doc-1"), "the un-expired document survives");
        engine.shutdown().expect("shuts down");
    }

    // ------------------------------------------------------------------
    // Refresh execution (V1 pin semantics)
    // ------------------------------------------------------------------

    #[test]
    fn refresh_execution_consumes_the_marker_and_leaves_expiry_untouched() {
        let (_dir, engine) = open_engine("refresh");
        let now = now_millis();
        put_user_document(
            &engine,
            "tenant-a",
            "put-refresh",
            "doc-refresh",
            user_value(&[("n", "v")]),
        );
        put_user_document(&engine, "tenant-a", "put-both", "doc-both", user_value(&[("n", "v")]));
        commit(&engine, "tenant-a", "lifecycle-expiry", |store, transaction, ts, id| {
            // V1 RefreshStorage: a due @refresh removes the marker and
            // re-puts the document; the @expires value is untouched.
            stage_expiration(store, transaction, ts, id, "doc-refresh", Some(now + 3_600_000));
            // A document due for both expiry and refresh: expiry wins (the
            // V1 deletion makes the refresh moot).
            stage_expiration(store, transaction, ts, id, "doc-both", Some(now - 1));
        });
        // One family mutation per record per transaction: the refresh
        // targets ride their own commit.
        commit(&engine, "tenant-a", "lifecycle-refresh", |store, transaction, ts, id| {
            stage_refresh(store, transaction, ts, id, "doc-refresh", Some(now - 1));
            stage_refresh(store, transaction, ts, id, "doc-both", Some(now - 1));
        });

        let pass =
            engine.families().plan_lifecycle_pass(&scope(&engine, "tenant-a")).expect("plans");
        assert_eq!(pass.refresh_due_documents(), [document_id("doc-refresh")]);
        assert_eq!(pass.expired_documents(), [document_id("doc-both")]);

        let staged = run_pass(&engine, "tenant-a", "pass-1");
        assert_eq!(staged.refreshed(), [document_id("doc-refresh")]);

        let state = lifecycle_state(&engine, "doc-refresh");
        assert_eq!(state.refresh_at(), None, "the due refresh marker is consumed");
        assert_eq!(
            state.expires_at(),
            Some(now + 3_600_000),
            "refresh consumption does not touch the expiry"
        );
        assert!(document_present(&engine, "tenant-a", "doc-refresh"));
        assert!(!document_present(&engine, "tenant-a", "doc-both"));
        engine.shutdown().expect("shuts down");
    }

    // ------------------------------------------------------------------
    // Read filtering of expired documents
    // ------------------------------------------------------------------

    #[test]
    fn expired_documents_are_not_visible_before_or_after_the_pass() {
        let (_dir, engine) = open_engine("visibility");
        let now = now_millis();
        put_user_document(
            &engine,
            "tenant-a",
            "put-expired",
            "doc-expired",
            user_value(&[("n", "v")]),
        );
        put_user_document(&engine, "tenant-a", "put-live", "doc-live", user_value(&[("n", "v")]));
        commit(&engine, "tenant-a", "expire-1", |store, transaction, ts, id| {
            stage_expiration(store, transaction, ts, id, "doc-expired", Some(now - 1));
            stage_expiration(store, transaction, ts, id, "doc-live", Some(now + 3_600_000));
        });

        // Before any maintenance runs, the expired-but-not-yet-deleted
        // document is already not visible: no partial-expiry state exists.
        assert_eq!(
            visibility(&engine, "tenant-a", &["doc-live", "doc-expired", "doc-missing"]),
            [VisibilityState::Visible, VisibilityState::Expired, VisibilityState::Missing]
        );

        run_pass(&engine, "tenant-a", "pass-1");
        assert_eq!(
            visibility(&engine, "tenant-a", &["doc-live", "doc-expired"]),
            [VisibilityState::Visible, VisibilityState::Missing]
        );
        engine.shutdown().expect("shuts down");
    }

    // ------------------------------------------------------------------
    // Archival execution
    // ------------------------------------------------------------------

    #[test]
    fn archival_policy_execution_materializes_then_archives() {
        let (_dir, engine) = open_engine("archival-exec");
        put_user_document(&engine, "tenant-a", "put-1", "doc-1", user_value(&[("n", "v")]));
        put_user_document(&engine, "tenant-a", "put-2", "doc-2", user_value(&[("n", "v")]));
        commit(&engine, "tenant-a", "archival-1", |store, transaction, ts, id| {
            stage_archival_policy(store, transaction, ts, id, "doc-1", 0);
            stage_archival_policy(store, transaction, ts, id, "doc-2", 3_600_000);
        });

        // First pass: a policy with no schedule materializes one.
        let staged = run_pass(&engine, "tenant-a", "pass-1");
        assert_eq!(staged.scheduled(), [document_id("doc-1"), document_id("doc-2")]);
        let state = archival_state(&engine, "doc-1").expect("state exists");
        assert!(!state.archived(), "materializing a schedule does not archive");
        assert!(state.at().is_some(), "the schedule time is materialized durably");
        assert_eq!(state.after_ms(), Some(0));

        // A long delay does not archive on the next pass; a zero delay is
        // due immediately.
        let staged = run_pass(&engine, "tenant-a", "pass-2");
        assert_eq!(
            staged.archived(),
            [document_id("doc-1")],
            "a zero-delay policy archives once its schedule is due"
        );
        assert!(staged.scheduled().is_empty());
        let state = archival_state(&engine, "doc-1").expect("state exists");
        assert!(state.archived());
        assert!(!archival_state(&engine, "doc-2").expect("state exists").archived());
        engine.shutdown().expect("shuts down");
    }

    #[test]
    fn stale_family_records_are_cleared_when_the_owner_is_gone() {
        let (_dir, engine) = open_engine("stale");
        let now = now_millis();
        put_user_document(&engine, "tenant-a", "put-1", "doc-1", user_value(&[("n", "v")]));
        commit(&engine, "tenant-a", "expire-1", |store, transaction, ts, id| {
            stage_expiration(store, transaction, ts, id, "doc-1", Some(now + 3_600_000));
        });
        commit(&engine, "tenant-a", "delete-1", |_, transaction, _, _| {
            transaction
                .delete(document_id("doc-1"), ExpectedVersion::Any)
                .expect("deletes the owner");
        });
        assert!(
            record_present(&engine, "lifecycle", "doc-1"),
            "W3-F keeps family state past a plain delete"
        );

        let pass =
            engine.families().plan_lifecycle_pass(&scope(&engine, "tenant-a")).expect("plans");
        assert_eq!(pass.stale_records(), [document_id("doc-1")]);
        let staged = run_pass(&engine, "tenant-a", "pass-1");
        assert_eq!(staged.cleared_stale(), [document_id("doc-1")]);
        assert!(!record_present(&engine, "lifecycle", "doc-1"));
        engine.shutdown().expect("shuts down");
    }

    #[test]
    fn the_pass_is_tenant_scoped() {
        let (_dir, engine) = open_engine("tenant-scope");
        let now = now_millis();
        put_user_document(&engine, "tenant-a", "put-a", "doc-a", user_value(&[("n", "v")]));
        put_user_document(&engine, "tenant-b", "put-b", "doc-b", user_value(&[("n", "v")]));
        commit(&engine, "tenant-b", "expire-b", |store, transaction, ts, id| {
            stage_expiration(store, transaction, ts, id, "doc-b", Some(now - 1));
        });

        let pass =
            engine.families().plan_lifecycle_pass(&scope(&engine, "tenant-a")).expect("plans");
        assert!(pass.is_empty(), "tenant-a's pass plans nothing for tenant-b");

        run_pass(&engine, "tenant-a", "pass-a");
        assert!(
            document_present(&engine, "tenant-b", "doc-b"),
            "the tenant-a pass never touches tenant-b state"
        );
        run_pass(&engine, "tenant-b", "pass-b");
        assert!(!document_present(&engine, "tenant-b", "doc-b"));
        engine.shutdown().expect("shuts down");
    }

    // ------------------------------------------------------------------
    // Repair loop 1 (warden findings, TerraBaseV2#75)
    // ------------------------------------------------------------------

    /// Runs one closure that must terminate, failing the test when it
    /// does not.
    ///
    /// The repair's red state cycled forever inside `plan_lifecycle_pass`
    /// (a busy paging loop, not a blocking wait), so the regression guard
    /// must bound the call itself instead of relying on the harness to
    /// kill it. A timed-out worker thread is left to process exit: the
    /// panic below already failed the test, and joining a spinning thread
    /// would hang the harness.
    fn must_terminate<T: Send + 'static>(tag: &str, run: impl FnOnce() -> T + Send + 'static) -> T {
        let (sender, receiver) = mpsc::channel();
        let worker = std::thread::Builder::new()
            .name(format!("tbv2-gapf06-{tag}"))
            .spawn(move || {
                let value = run();
                let _ = sender.send(value);
            })
            .expect("spawns the guarded worker");
        match receiver.recv_timeout(Duration::from_secs(30)) {
            Ok(value) => {
                worker.join().expect("the guarded worker ends");
                value
            }
            Err(mpsc::RecvTimeoutError::Timeout) => {
                panic!("`{tag}` did not terminate within 30 s");
            }
            Err(mpsc::RecvTimeoutError::Disconnected) => {
                let _ = worker.join();
                panic!("`{tag}` ended without reporting a value");
            }
        }
    }

    #[test]
    fn the_plan_terminates_and_covers_a_tenant_that_reuses_one_id_across_collections() {
        // Warden MAJOR: the windowed paging resumed strictly after the
        // FIRST canonical key bearing the cursor identifier, so a tenant
        // with the same document id in two collections cycled the plan
        // forever (src/storage/root.rs documents the shape; migrations and
        // streaming bound the same seam). The plan must terminate — and
        // still see the reused id's lifecycle state.
        let (planned, shutdown) = must_terminate("dup-id", || {
            let (_dir, engine) = open_engine("dup-id");
            let put = |operation: &str, collection: &str| {
                commit(&engine, "tenant-a", operation, |_, transaction, _, _| {
                    transaction
                        .put(
                            Document::new(
                                document_id("doc-x"),
                                CollectionName::try_new(collection).expect("collection"),
                                user_value(&[("n", "v")]),
                            ),
                            ExpectedVersion::Any,
                        )
                        .expect("puts");
                });
            };
            put("put-users", "users");
            put("put-zzz", "zzz");
            let now = now_millis();
            commit(&engine, "tenant-a", "expire", |store, transaction, ts, id| {
                stage_expiration(store, transaction, ts, id, "doc-x", Some(now - 1));
            });
            let planned = engine.families().plan_lifecycle_pass(&scope(&engine, "tenant-a"));
            (planned, engine.shutdown())
        });
        shutdown.expect("shuts down");
        let pass = planned.expect("the plan over a reused identifier completes");
        assert_eq!(
            pass.expired_documents(),
            [document_id("doc-x")],
            "the reused id's elapsed expiry is still planned"
        );
        assert!(pass.refresh_due_documents().is_empty());
        assert!(pass.scheduled_archival_documents().is_empty());
        assert!(pass.archive_due_documents().is_empty());
        assert!(pass.stale_records().is_empty());
    }

    #[test]
    fn a_non_object_lifecycle_record_value_fails_the_pass_closed() {
        // Warden MINOR: the shared decoder reads every field off a
        // non-object value as absent, so a squatter string at the reserved
        // lifecycle identifier decoded as "no lifecycle state" and the
        // pass returned Ok. A malformed reserved value must fail the pass
        // closed, like every other reserved-state collision.
        let (_dir, engine) = open_engine("squatter");
        let squatter_id = record_id("lifecycle", &["doc-1"]).expect("record id");
        commit(&engine, "tenant-a", "squatter-put", |_, transaction, _, _| {
            transaction
                .put(
                    Document::new(
                        squatter_id.clone(),
                        CollectionName::try_new(RESERVED_COLLECTION).expect("collection"),
                        DocumentValue::String("not family state".to_owned()),
                    ),
                    ExpectedVersion::Any,
                )
                .expect("the engine accepts a squatter document");
        });
        let error = engine
            .families()
            .plan_lifecycle_pass(&scope(&engine, "tenant-a"))
            .expect_err("a malformed reserved value fails the pass closed");
        assert!(matches!(error, FamilyError::ReservedStateInvalid));
        engine.shutdown().expect("shuts down");
    }

    #[test]
    fn stale_owner_lists_stay_unique_when_owners_have_both_record_kinds() {
        // Warden MINOR: canonical key order places every `family:archival/*`
        // record before every `family:lifecycle/*` record, so one gone
        // owner with both record kinds reached the stale list twice and
        // the adjacent-duplicates guard could not see the repeat.
        let (_dir, engine) = open_engine("stale-unique");
        put_user_document(&engine, "tenant-a", "put-1", "doc-1", user_value(&[("n", "v")]));
        put_user_document(&engine, "tenant-a", "put-2", "doc-2", user_value(&[("n", "v")]));
        let now = now_millis();
        commit(&engine, "tenant-a", "configure", |store, transaction, ts, id| {
            stage_expiration(store, transaction, ts, id, "doc-1", Some(now + 3_600_000));
            stage_expiration(store, transaction, ts, id, "doc-2", Some(now + 3_600_000));
        });
        commit(&engine, "tenant-a", "archival", |store, transaction, ts, id| {
            stage_archival_policy(store, transaction, ts, id, "doc-1", 3_600_000);
            stage_archival_policy(store, transaction, ts, id, "doc-2", 3_600_000);
        });
        commit(&engine, "tenant-a", "delete-owners", |_, transaction, _, _| {
            transaction
                .delete(document_id("doc-1"), ExpectedVersion::Any)
                .expect("deletes the first owner");
            transaction
                .delete(document_id("doc-2"), ExpectedVersion::Any)
                .expect("deletes the second owner");
        });

        let pass =
            engine.families().plan_lifecycle_pass(&scope(&engine, "tenant-a")).expect("plans");
        assert_eq!(pass.stale_records(), [document_id("doc-1"), document_id("doc-2")]);
        assert!(pass.expired_documents().is_empty());
        let staged = stage_pass(&engine, "tenant-a", "pass-1", &pass);
        assert_eq!(staged.cleared_stale(), [document_id("doc-1"), document_id("doc-2")]);
        assert!(!record_present(&engine, "lifecycle", "doc-1"));
        assert!(!record_present(&engine, "lifecycle", "doc-2"));
        assert!(!record_present(&engine, "archival", "doc-1"));
        assert!(!record_present(&engine, "archival", "doc-2"));
        engine.shutdown().expect("shuts down");
    }

    // ------------------------------------------------------------------
    // Backup and restore interactions (BK wave)
    // ------------------------------------------------------------------

    /// Opens a restored engine and reads the tenant's documents at the
    /// newest snapshot.
    fn restored_documents(target: &Path, tenant_name: &str) -> Vec<(String, DocumentValue)> {
        let engine =
            Engine::open(EngineConfig::durable(target).expect("durable location")).expect("engine");
        let tenant_scope = scope(&engine, tenant_name);
        let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
        let documents = engine.snapshot_documents(&tenant_scope, &snapshot).expect("documents");
        let named = documents
            .into_iter()
            .map(|document| (document.id().as_str().to_owned(), document.value().clone()))
            .collect::<Vec<_>>();
        engine.shutdown().expect("shuts down");
        named
    }

    #[test]
    fn full_backup_and_restore_reproduce_archived_state_and_expired_deletions() {
        let (_dir, engine) = open_engine("bk-full");
        let now = now_millis();
        put_user_document(&engine, "tenant-a", "put-live", "doc-live", user_value(&[("n", "v")]));
        put_user_document(
            &engine,
            "tenant-a",
            "put-expired",
            "doc-expired",
            user_value(&[("n", "v")]),
        );
        put_user_document(
            &engine,
            "tenant-a",
            "put-archived",
            "doc-archived",
            user_value(&[("n", "v")]),
        );
        commit(&engine, "tenant-a", "configure", |store, transaction, ts, id| {
            stage_expiration(store, transaction, ts, id, "doc-expired", Some(now - 1));
            stage_expiration(store, transaction, ts, id, "doc-live", Some(now + 3_600_000));
            store
                .stage(
                    transaction,
                    FamilyWriteRequest::archival_update(
                        ArchivalWrite::try_new(
                            ts.clone(),
                            document_id("doc-archived"),
                            ArchivalCommand::Archive { at: now },
                            id.clone(),
                            deadline(),
                            cancellation(),
                        )
                        .expect("valid archival write"),
                    ),
                )
                .expect("stages the archival mark");
        });
        run_pass(&engine, "tenant-a", "pass-1");

        let destination = fresh_dir("bk-full-dest");
        let result = run_backup(&engine, BackupPlan::full(), &destination).expect("backs up");
        // A full backup archives live state, not history: the deleted
        // document is simply absent, and no delete record exists.
        assert_eq!(result.manifest().deletes(), 0);
        assert_eq!(
            result.manifest().documents(),
            2,
            "only the live and archived documents remain in the state archive"
        );
        assert!(
            result.manifest().family_records() >= 1,
            "family records — including the archived state — ride the archive"
        );

        let target = fresh_dir("bk-full-target");
        RestorePlan::restore_latest(&destination, &target).expect("restores");
        let documents = restored_documents(&target, "tenant-a");
        let ids = documents.iter().map(|(id, _)| id.as_str()).collect::<Vec<_>>();
        assert!(ids.contains(&"doc-live"), "the live document restores");
        assert!(
            !ids.contains(&"doc-expired"),
            "the expired document deleted by the pass does not restore"
        );
        assert!(
            ids.contains(&"doc-archived"),
            "an archived document is retained state, not deleted state"
        );
        engine.shutdown().expect("shuts down");
    }

    #[test]
    fn an_incremental_chain_replays_the_pass_deletions_on_restore() {
        let (_dir, engine) = open_engine("bk-inc");
        let now = now_millis();
        put_user_document(&engine, "tenant-a", "put-live", "doc-live", user_value(&[("n", "v")]));
        put_user_document(
            &engine,
            "tenant-a",
            "put-expired",
            "doc-expired",
            user_value(&[("n", "v")]),
        );
        commit(&engine, "tenant-a", "configure", |store, transaction, ts, id| {
            stage_expiration(store, transaction, ts, id, "doc-expired", Some(now - 1));
        });

        // The base is taken while the expired document still exists.
        let destination = fresh_dir("bk-inc-dest");
        let base = run_backup(&engine, BackupPlan::full(), &destination).expect("base backup");
        let boundary = base.manifest().boundary_sequence();

        run_pass(&engine, "tenant-a", "pass-1");

        let incremental =
            run_backup(&engine, BackupPlan::incremental_since(boundary), &destination)
                .expect("incremental backup");
        assert!(
            incremental.manifest().deletes() >= 1,
            "the pass's delete publications ride the incremental link"
        );

        let target = fresh_dir("bk-inc-target");
        RestorePlan::restore_latest(&destination, &target).expect("restores the chain");
        let documents = restored_documents(&target, "tenant-a");
        let ids = documents.iter().map(|(id, _)| id.as_str()).collect::<Vec<_>>();
        assert!(ids.contains(&"doc-live"));
        assert!(
            !ids.contains(&"doc-expired"),
            "restoring the chain applies the pass deletions exactly"
        );
        engine.shutdown().expect("shuts down");
    }

    // ------------------------------------------------------------------
    // Pass vocabulary construction
    // ------------------------------------------------------------------

    #[test]
    fn visibility_reads_validate_typed_fields_before_observation() {
        assert_eq!(
            VisibilityRead::try_new(Vec::new(), deadline(), cancellation()).err(),
            Some(RequestValidationError::InvalidFamilyPayload)
        );
        let elapsed = Deadline::after(Duration::from_millis(1)).expect("valid deadline");
        std::thread::sleep(Duration::from_millis(40));
        assert_eq!(
            VisibilityRead::try_new(vec![document_id("doc-1")], elapsed, cancellation()).err(),
            Some(RequestValidationError::InvalidDeadline)
        );
        let cancelled = Cancellation::new();
        cancelled.cancel();
        assert_eq!(
            VisibilityRead::try_new(vec![document_id("doc-1")], deadline(), cancelled).err(),
            Some(RequestValidationError::InvalidCancellation)
        );
    }
}
