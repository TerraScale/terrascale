//! Lifecycle and archival execution (GAP-F06, TerraBaseV2#75).
//!
//! Declaration-only module root wired by the integrator so the GAP-F06
//! packet owns this file's contents without editing `src/families/mod.rs`.
//! The packet implements expiry deletion execution, expiration-refresh
//! family behavior, read filtering of expired documents, and archival
//! execution with its backup/retention interactions, recording the
//! execution-owner decision. It introduces no behavior until that packet
//! lands.

use crate::engine::Transaction;
use crate::families::state::{now_millis, record_id};
use crate::families::{FamilyError, FamilyReadContext, FamilyStore};
use crate::operations::RequestValidationError;
use crate::types::{Cancellation, Deadline, DocumentId, TenantScope};

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
// Execution
// ---------------------------------------------------------------------------

impl<'engine> FamilyStore<'engine> {
    /// Plans one lifecycle maintenance pass for one tenant scope.
    ///
    /// # Errors
    ///
    /// Returns [`FamilyError::TenantMismatch`],
    /// [`FamilyError::SnapshotEngineMismatch`], or
    /// [`FamilyError::StorageFailure`] for scope and snapshot failures, and
    /// [`FamilyError::ReservedStateInvalid`] when a reserved record cannot
    /// be read as family state.
    pub fn plan_lifecycle_pass(
        &self,
        scope: &TenantScope,
    ) -> Result<LifecyclePass, FamilyError> {
        let _ = record_id("lifecycle", &[])?;
        Ok(LifecyclePass::new(scope.clone()))
    }

    /// Stages one planned lifecycle pass into the caller's transaction.
    ///
    /// # Errors
    ///
    /// Returns [`FamilyError::TransactionEngineMismatch`] or
    /// [`FamilyError::TenantMismatch`] when the transaction is not live
    /// under the pass's scope, and the family-specific case for a rejected
    /// action.
    pub fn stage_lifecycle_pass(
        &self,
        transaction: &mut Transaction<'engine>,
        pass: &LifecyclePass,
    ) -> Result<LifecyclePassStaged, FamilyError> {
        let _ = now_millis();
        let _ = transaction;
        Ok(LifecyclePassStaged::default())
    }

    /// Reads one batch of documents' visibility through the lifecycle lens.
    ///
    /// # Errors
    ///
    /// Returns [`FamilyError::TenantMismatch`] or
    /// [`FamilyError::SnapshotEngineMismatch`] for an invalid context,
    /// [`FamilyError::DeadlineExceeded`] or [`FamilyError::Cancelled`]
    /// before the read runs, and
    /// [`FamilyError::ReservedStateInvalid`] when a reserved record cannot
    /// be read as family state.
    pub fn read_document_visibility(
        &self,
        context: FamilyReadContext,
        request: VisibilityRead,
    ) -> Result<VisibilityReport, FamilyError> {
        let _ = FamilyReadContext::validate(context.tenant(), context.snapshot())?;
        if request.deadline.is_expired() {
            return Err(FamilyError::DeadlineExceeded);
        }
        if request.cancellation.is_cancelled() {
            return Err(FamilyError::Cancelled);
        }
        let _ = record_id("lifecycle", &[])?;
        let entries = request
            .documents
            .iter()
            .map(|document| DocumentVisibility::new(document.clone(), VisibilityState::Missing))
            .collect();
        Ok(VisibilityReport::new(entries))
    }
}

#[cfg(test)]
mod tests {
    //! Focused GAP-F06 tests: the red suite first pinned the contract
    //! against a naive baseline (an empty pass and no visibility
    //! filtering); every test below discriminates that baseline.

    use std::path::{Path, PathBuf};
    use std::time::Duration;

    use super::super::payloads::{ArchivalWrite, LifecycleWrite};
    use super::super::state::{Records, now_millis, record_id};
    use super::super::{
        ArchivalCommand, ArchivalReadRequest, FamilyError, FamilyReadContext, FamilyReadRequest,
        FamilyReadResult, FamilyWriteRequest, LifecycleCommand, LifecycleRead, TombstoneCommand,
        TombstoneRead, TombstoneWrite,
    };
    use super::{LifecyclePass, VisibilityRead, VisibilityState};
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
        transaction.commit(LocalDurability::Synced)
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
                FamilyWriteRequest::lifecycle_update(LifecycleWrite::try_new(
                    tenant_scope.clone(),
                    document_id(id),
                    LifecycleCommand::SetExpiration { at },
                    ExpectedVersion::Any,
                    identity.clone(),
                    deadline(),
                    cancellation(),
                )
                .expect("valid lifecycle write")),
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
                FamilyWriteRequest::lifecycle_update(LifecycleWrite::try_new(
                    tenant_scope.clone(),
                    document_id(id),
                    LifecycleCommand::SetRefresh { at },
                    ExpectedVersion::Any,
                    identity.clone(),
                    deadline(),
                    cancellation(),
                )
                .expect("valid lifecycle write")),
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
                FamilyWriteRequest::archival_update(ArchivalWrite::try_new(
                    tenant_scope.clone(),
                    document_id(id),
                    ArchivalCommand::SetPolicy { after_ms },
                    identity.clone(),
                    deadline(),
                    cancellation(),
                )
                .expect("valid archival write")),
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

    /// Runs one full pass: plan, stage into a fresh transaction, commit.
    fn run_pass(engine: &Engine, tenant_name: &str, operation_name: &str) -> super::LifecyclePassStaged {
        let pass = engine.families().plan_lifecycle_pass(&scope(engine, tenant_name))
            .expect("plans the pass");
        let mut staged = None;
        commit(engine, tenant_name, operation_name, |store, transaction, _, _| {
            staged = Some(store.stage_lifecycle_pass(transaction, &pass).expect("stages the pass"));
        });
        staged.expect("the closure ran")
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
        report.entries().iter().map(|entry| entry.state()).collect()
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
        let now = super::super::state::now_millis();
        put_user_document(&engine, "tenant-a", "put-expired", "doc-expired", user_value(&[("n", "v")]));
        put_user_document(&engine, "tenant-a", "put-live", "doc-live", user_value(&[("n", "v")]));
        commit(&engine, "tenant-a", "expire-1", |store, transaction, ts, id| {
            stage_expiration(store, transaction, ts, id, "doc-expired", Some(now - 1));
            stage_expiration(store, transaction, ts, id, "doc-live", Some(now + 3_600_000));
        });

        let pass = engine
            .families()
            .plan_lifecycle_pass(&scope(&engine, "tenant-a"))
            .expect("plans");
        assert_eq!(pass.expired_documents(), [document_id("doc-expired")]);
        assert!(pass.refresh_due_documents().is_empty());

        let staged = run_pass(&engine, "tenant-a", "pass-1");
        assert_eq!(staged.deleted(), [document_id("doc-expired")]);

        assert!(!document_present(&engine, "tenant-a", "doc-expired"));
        assert!(document_present(&engine, "tenant-a", "doc-live"), "only the expired document dies");
        let state = lifecycle_state(&engine, "doc-expired");
        assert!(!state.present(), "the deleted document is not resurrected");
        assert!(!record_present(&engine, "lifecycle", "doc-expired"),
            "the lifecycle record is cleared with the deletion");

        // The deletion records family evidence: a tombstone for the
        // lifecycle family fences stale writers.
        let tombstones = match read_family(
            &engine,
            "tenant-a",
            FamilyReadRequest::tombstones(
                TombstoneRead::try_new(document_id("doc-expired"), deadline(), cancellation())
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
        let now = super::super::state::now_millis();
        put_user_document(&engine, "tenant-a", "put-1", "doc-1", user_value(&[("n", "v1")]));
        commit(&engine, "tenant-a", "expire-1", |store, transaction, ts, id| {
            stage_expiration(store, transaction, ts, id, "doc-1", Some(now - 1));
        });

        let pass = engine
            .families()
            .plan_lifecycle_pass(&scope(&engine, "tenant-a"))
            .expect("plans against version 1");
        // A writer lands between the plan and the stage: the document is
        // re-put at a new version with the expiry cleared.
        commit(&engine, "tenant-a", "concurrent-put", |store, transaction, ts, id| {
            stage_expiration(store, transaction, ts, id, "doc-1", None);
        });

        let mut staged = None;
        commit(&engine, "tenant-a", "pass-1", |store, transaction, _, _| {
            staged = Some(store.stage_lifecycle_pass(transaction, &pass).expect("stages"));
        });
        let staged = staged.expect("the closure ran");
        assert_eq!(staged.skipped(), [document_id("doc-1")],
            "a document that changed after the plan is not deleted by the stale plan");
        assert!(staged.deleted().is_empty());
        assert!(document_present(&engine, "tenant-a", "doc-1"),
            "the refreshed-cleared document survives");
        engine.shutdown().expect("shuts down");
    }

    // ------------------------------------------------------------------
    // Refresh execution (V1 pin semantics)
    // ------------------------------------------------------------------

    #[test]
    fn refresh_execution_consumes_the_marker_and_leaves_expiry_untouched() {
        let (_dir, engine) = open_engine("refresh");
        let now = super::super::state::now_millis();
        put_user_document(&engine, "tenant-a", "put-refresh", "doc-refresh", user_value(&[("n", "v")]));
        put_user_document(&engine, "tenant-a", "put-both", "doc-both", user_value(&[("n", "v")]));
        commit(&engine, "tenant-a", "lifecycle-1", |store, transaction, ts, id| {
            // V1 RefreshStorage: a due @refresh removes the marker and
            // re-puts the document; the @expires value is untouched.
            stage_expiration(store, transaction, ts, id, "doc-refresh", Some(now + 3_600_000));
            stage_refresh(store, transaction, ts, id, "doc-refresh", Some(now - 1));
            // A document due for both expiry and refresh: expiry wins (the
            // V1 deletion makes the refresh moot).
            stage_expiration(store, transaction, ts, id, "doc-both", Some(now - 1));
            stage_refresh(store, transaction, ts, id, "doc-both", Some(now - 1));
        });

        let pass = engine
            .families()
            .plan_lifecycle_pass(&scope(&engine, "tenant-a"))
            .expect("plans");
        assert_eq!(pass.refresh_due_documents(), [document_id("doc-refresh")]);
        assert_eq!(pass.expired_documents(), [document_id("doc-both")]);

        let staged = run_pass(&engine, "tenant-a", "pass-1");
        assert_eq!(staged.refreshed(), [document_id("doc-refresh")]);

        let state = lifecycle_state(&engine, "doc-refresh");
        assert_eq!(state.refresh_at(), None, "the due refresh marker is consumed");
        assert_eq!(state.expires_at(), Some(now + 3_600_000),
            "refresh consumption does not touch the expiry");
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
        let now = super::super::state::now_millis();
        put_user_document(&engine, "tenant-a", "put-expired", "doc-expired", user_value(&[("n", "v")]));
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

        // A long delay does not archive on the next pass.
        let staged = run_pass(&engine, "tenant-a", "pass-2");
        assert_eq!(staged.archived(), [document_id("doc-1")],
            "a zero-delay policy archives once its schedule is due");
        assert!(staged.scheduled().is_empty());
        let state = archival_state(&engine, "doc-1").expect("state exists");
        assert!(state.archived());
        assert!(!archival_state(&engine, "doc-2").expect("state exists").archived());
        engine.shutdown().expect("shuts down");
    }

    #[test]
    fn stale_family_records_are_cleared_when_the_owner_is_gone() {
        let (_dir, engine) = open_engine("stale");
        let now = super::super::state::now_millis();
        put_user_document(&engine, "tenant-a", "put-1", "doc-1", user_value(&[("n", "v")]));
        commit(&engine, "tenant-a", "expire-1", |store, transaction, ts, id| {
            stage_expiration(store, transaction, ts, id, "doc-1", Some(now + 3_600_000));
        });
        commit(&engine, "tenant-a", "delete-1", |_, transaction, _, _| {
            transaction
                .delete(document_id("doc-1"), ExpectedVersion::Any)
                .expect("deletes the owner");
        });
        assert!(record_present(&engine, "lifecycle", "doc-1"),
            "W3-F keeps family state past a plain delete");

        let pass = engine
            .families()
            .plan_lifecycle_pass(&scope(&engine, "tenant-a"))
            .expect("plans");
        assert_eq!(pass.stale_records(), [document_id("doc-1")]);
        let staged = run_pass(&engine, "tenant-a", "pass-1");
        assert_eq!(staged.cleared_stale(), [document_id("doc-1")]);
        assert!(!record_present(&engine, "lifecycle", "doc-1"));
        engine.shutdown().expect("shuts down");
    }

    #[test]
    fn the_pass_is_tenant_scoped() {
        let (_dir, engine) = open_engine("tenant-scope");
        let now = super::super::state::now_millis();
        put_user_document(&engine, "tenant-a", "put-a", "doc-a", user_value(&[("n", "v")]));
        put_user_document(&engine, "tenant-b", "put-b", "doc-b", user_value(&[("n", "v")]));
        commit(&engine, "tenant-b", "expire-b", |store, transaction, ts, id| {
            stage_expiration(store, transaction, ts, id, "doc-b", Some(now - 1));
        });

        let pass = engine
            .families()
            .plan_lifecycle_pass(&scope(&engine, "tenant-a"))
            .expect("plans");
        assert!(pass.is_empty(), "tenant-a's pass plans nothing for tenant-b");

        run_pass(&engine, "tenant-a", "pass-a");
        assert!(document_present(&engine, "tenant-b", "doc-b"),
            "the tenant-a pass never touches tenant-b state");
        run_pass(&engine, "tenant-b", "pass-b");
        assert!(!document_present(&engine, "tenant-b", "doc-b"));
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
        let now = super::super::state::now_millis();
        put_user_document(&engine, "tenant-a", "put-live", "doc-live", user_value(&[("n", "v")]));
        put_user_document(&engine, "tenant-a", "put-expired", "doc-expired", user_value(&[("n", "v")]));
        put_user_document(&engine, "tenant-a", "put-archived", "doc-archived", user_value(&[("n", "v")]));
        commit(&engine, "tenant-a", "configure", |store, transaction, ts, id| {
            stage_expiration(store, transaction, ts, id, "doc-expired", Some(now - 1));
            stage_expiration(store, transaction, ts, id, "doc-live", Some(now + 3_600_000));
            store
                .stage(
                    transaction,
                    FamilyWriteRequest::archival_update(ArchivalWrite::try_new(
                        ts.clone(),
                        document_id("doc-archived"),
                        ArchivalCommand::Archive { at: now },
                        id.clone(),
                        deadline(),
                        cancellation(),
                    )
                    .expect("valid archival write")),
                )
                .expect("stages the archival mark");
        });
        run_pass(&engine, "tenant-a", "pass-1");

        let destination = fresh_dir("bk-full-dest");
        let result =
            run_backup(&engine, BackupPlan::full(), &destination).expect("backs up");
        assert!(result.manifest().deletes() >= 1,
            "the pass deletions are journaled deletes the archive carries");
        assert!(result.manifest().family_records() >= 1,
            "family records — including the archived state — ride the archive");

        let target = fresh_dir("bk-full-target");
        RestorePlan::restore_latest(&destination, &target).expect("restores");
        let documents = restored_documents(&target, "tenant-a");
        let ids = documents.iter().map(|(id, _)| id.as_str()).collect::<Vec<_>>();
        assert!(ids.contains(&"doc-live"), "the live document restores");
        assert!(!ids.contains(&"doc-expired"),
            "the expired document deleted by the pass does not restore");
        assert!(ids.contains(&"doc-archived"),
            "an archived document is retained state, not deleted state");
        engine.shutdown().expect("shuts down");
    }

    #[test]
    fn an_incremental_chain_replays_the_pass_deletions_on_restore() {
        let (_dir, engine) = open_engine("bk-inc");
        let now = super::super::state::now_millis();
        put_user_document(&engine, "tenant-a", "put-live", "doc-live", user_value(&[("n", "v")]));
        put_user_document(&engine, "tenant-a", "put-expired", "doc-expired", user_value(&[("n", "v")]));
        commit(&engine, "tenant-a", "configure", |store, transaction, ts, id| {
            stage_expiration(store, transaction, ts, id, "doc-expired", Some(now - 1));
        });

        // The base is taken while the expired document still exists.
        let destination = fresh_dir("bk-inc-dest");
        let base = run_backup(&engine, BackupPlan::full(), &destination).expect("base backup");
        let boundary = base.manifest().boundary_sequence();

        run_pass(&engine, "tenant-a", "pass-1");

        let incremental = run_backup(
            &engine,
            BackupPlan::incremental_since(boundary),
            &destination,
        )
        .expect("incremental backup");
        assert!(incremental.manifest().deletes() >= 1,
            "the pass's delete publications ride the incremental link");

        let target = fresh_dir("bk-inc-target");
        RestorePlan::restore_latest(&destination, &target).expect("restores the chain");
        let documents = restored_documents(&target, "tenant-a");
        let ids = documents.iter().map(|(id, _)| id.as_str()).collect::<Vec<_>>();
        assert!(ids.contains(&"doc-live"));
        assert!(!ids.contains(&"doc-expired"),
            "restoring the chain applies the pass deletions exactly");
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
