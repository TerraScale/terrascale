//! The query-to-write bridge: patch-by-query and delete-by-query (GAP-Q10,
//! ticket #65; decision record `docs/query/gapq10-bulk-by-query.md`).
//!
//! A bulk-by-query run compiles the caller's typed [`QuerySpec`] as the
//! **selection**, resolves the matched document identities against one
//! immutable snapshot in bounded pages, and publishes the writes as **one
//! engine transaction per bounded page** — one derived [`OperationId`], one
//! atomic commit, all or nothing — V1's per-batch transaction shape
//! (`AbstractDatabaseQueryRunner.cs:184-196`, batches of 4096 at `:153`,
//! pinned V1 tree). The patch half's per-document body is exactly the
//! GAP-D07 typed [`DocumentPatch`] applied through
//! [`apply_document_patch`](crate::engine::apply_document_patch); there is
//! no second patch language and no script body (charter
//! `PD-2026-08-26-08` D2). The delete half stages through
//! [`Transaction::delete_in_collection`](crate::engine::Transaction), the
//! one engine hook this packet adds, because a bulk selection targets one
//! collection and must resolve its deletions by the exact key.
//!
//! Every run reconciles exactly:
//! `selected == processed + not_modified + refused + unattempted`, so a run
//! that stops anywhere never claims a document it did not publish. The
//! delete half refuses without an explicit [`DeletionConfirmation`] token
//! before any data access (PD-2026-08-26-07's confirmable-mutation rule at
//! a non-interactive seam), and every run emits content-free audit entries
//! into the engine's bounded journal. No performance claim is made or
//! implied by anything here.

use std::sync::{
    Mutex,
    atomic::{AtomicU64, Ordering},
};

use super::{
    QueryError, QueryInput, QueryPaging, QuerySpec, QueryStore, StalenessReport,
    execute::check_bounds, spec::MAX_PAGE_LIMIT,
};
use crate::engine::{
    CommitOutcome, DocumentPatch, EngineError, PatchError, PatchOutcome, apply_document_patch,
};
use crate::operations::RequestValidationError;
use crate::types::{
    Cancellation, CollectionName, Deadline, DocumentId, ExpectedVersion, LocalDurability,
    OperationId, StorageSequence, TenantId, TenantScope,
};

/// The maximum number of documents one write page holds.
///
/// The bound mirrors V1's server-side bulk-operation batch size
/// (`AbstractDatabaseQueryRunner.cs:153`, pinned V1 tree). The engine's
/// per-document limits already bound bytes, so no second byte bound exists.
pub const MAX_BULK_PAGE_DOCUMENTS: usize = 4096;

/// The maximum number of per-document detail records one receipt retains.
///
/// Details are always counted exactly through the reconciling counters; the
/// records ride a bounded drop-oldest window with the overflow counted and
/// disclosed — the bounded-disclosure idiom GAP-D10 set for bulk receipts.
pub const MAX_BULK_DETAILS: usize = 64;

/// The maximum number of audit events the engine's bulk journal retains.
///
/// The bound keeps recording bounded and non-blocking: once the journal is
/// full, the oldest event is dropped in favor of the newest, and the
/// monotonic sequence keeps the truncation observable. This mirrors the
/// Platform adapter's audit skeleton bound.
pub const MAX_BULK_AUDIT_ENTRIES: usize = 1024;

/// Shared execution options of both bulk-by-query halves.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct BulkByQueryOptions {
    page_documents: usize,
    details: bool,
}

impl BulkByQueryOptions {
    /// Creates the default options: the maximum write-page bound and no
    /// per-document detail window (V1's `RetrieveDetails` defaults to
    /// false, `QueryOperationOptions.cs:49`, pinned V1 tree).
    #[must_use]
    pub const fn new() -> Self {
        Self { page_documents: MAX_BULK_PAGE_DOCUMENTS, details: false }
    }

    /// Selects the write-page bound: how many selected documents one
    /// transaction publishes.
    ///
    /// # Errors
    ///
    /// Returns [`RequestValidationError::InvalidRange`] for a zero bound or
    /// one over [`MAX_BULK_PAGE_DOCUMENTS`].
    pub fn try_with_page_documents(
        mut self,
        page_documents: usize,
    ) -> Result<Self, RequestValidationError> {
        if page_documents == 0 || page_documents > MAX_BULK_PAGE_DOCUMENTS {
            return Err(RequestValidationError::InvalidRange);
        }
        self.page_documents = page_documents;
        Ok(self)
    }

    /// Selects per-document detail disclosure (V1's `RetrieveDetails`).
    #[must_use]
    pub const fn with_details(mut self, details: bool) -> Self {
        self.details = details;
        self
    }

    /// Returns the write-page bound.
    #[must_use]
    pub const fn page_documents(self) -> usize {
        self.page_documents
    }

    /// Reports whether per-document detail records are disclosed.
    #[must_use]
    pub const fn details(self) -> bool {
        self.details
    }
}

impl Default for BulkByQueryOptions {
    fn default() -> Self {
        Self::new()
    }
}

/// The explicit confirmation token a delete-by-query request must carry.
///
/// The engine seam is non-interactive (charter `PD-2026-08-26-11`), so the
/// confirm step surfaces as a typed token the caller must name at the call
/// site — the same discipline that makes GAP-D10's destructive
/// `IngestConflictMode::Overwrite` an explicit selection. A delete request
/// without a token refuses before any data access with
/// [`BulkRefusal::ConfirmationRequired`]. The interactive prompt a human
/// caller sees belongs to the host/Studio surface; an agent caller passes
/// the token directly (PD-2026-08-26-07).
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct DeletionConfirmation {
    _private: (),
}

impl DeletionConfirmation {
    /// Constructs the explicit confirmation token.
    ///
    /// The name is the confirmation: there is no default, no implicit
    /// construction, and no way to reach a deletion without spelling this
    /// call.
    #[must_use]
    pub const fn confirm_deletions() -> Self {
        Self { _private: () }
    }
}

/// A typed delete-by-query request: the selection specification, the
/// execution options, and the durability point each page commits to.
///
/// The confirmation token and the stable run identity are additive: a
/// request constructed without them refuses deletion (no token) and derives
/// its page identities from a minted run identity.
#[derive(Clone, Debug, PartialEq)]
pub struct DeleteByQueryRequest {
    query: QuerySpec,
    options: BulkByQueryOptions,
    durability: LocalDurability,
    operation_id: Option<OperationId>,
    confirmation: Option<DeletionConfirmation>,
}

impl DeleteByQueryRequest {
    /// Creates a request after re-checking the liveness bound the
    /// specification carries.
    ///
    /// The selection specification arrives pre-validated through
    /// [`QuerySpec::try_new`], so this constructor repeats only the bounded
    /// liveness checks the request now owns.
    ///
    /// # Errors
    ///
    /// Returns [`RequestValidationError::InvalidDeadline`] for an already
    /// elapsed deadline and [`RequestValidationError::InvalidCancellation`]
    /// for an already cancelled signal.
    pub fn try_new(
        query: QuerySpec,
        options: BulkByQueryOptions,
        durability: LocalDurability,
    ) -> Result<Self, RequestValidationError> {
        validate_liveness(query.deadline(), &query.cancellation())?;
        Ok(Self { query, options, durability, operation_id: None, confirmation: None })
    }

    /// Names the stable run identity: the identity the audit entries carry
    /// and every page identity derives under.
    #[must_use]
    pub fn with_operation_id(mut self, operation_id: OperationId) -> Self {
        self.operation_id = Some(operation_id);
        self
    }

    /// Attaches the explicit confirmation token that authorizes deletion.
    #[must_use]
    pub fn with_confirmation(mut self, confirmation: DeletionConfirmation) -> Self {
        self.confirmation = Some(confirmation);
        self
    }

    /// Returns the selection specification.
    #[must_use]
    pub const fn query(&self) -> &QuerySpec {
        &self.query
    }

    /// Returns the execution options.
    #[must_use]
    pub const fn options(&self) -> BulkByQueryOptions {
        self.options
    }

    /// Returns the durability point each page commits to.
    #[must_use]
    pub const fn durability(&self) -> LocalDurability {
        self.durability
    }

    /// Returns the stable run identity, when the caller named one.
    #[must_use]
    pub const fn operation_id(&self) -> Option<&OperationId> {
        self.operation_id.as_ref()
    }

    /// Returns the confirmation token, when the caller attached one.
    #[must_use]
    pub const fn confirmation(&self) -> Option<&DeletionConfirmation> {
        self.confirmation.as_ref()
    }
}

/// A typed patch-by-query request: the selection specification, the GAP-D07
/// patch applied to every match, and the execution options.
///
/// The patch is data, not a program: it is the same
/// [`DocumentPatch`](crate::engine::DocumentPatch) vocabulary GAP-D07
/// defined, applied per matched document under
/// [`ExpectedVersion::Any`] — V1's patch-by-query runs its command with a
/// null change vector (`AbstractDatabaseQueryRunner.cs:120`, pinned V1
/// tree).
#[derive(Clone, Debug, PartialEq)]
pub struct PatchByQueryRequest {
    query: QuerySpec,
    patch: DocumentPatch,
    options: BulkByQueryOptions,
    durability: LocalDurability,
    operation_id: Option<OperationId>,
}

impl PatchByQueryRequest {
    /// Creates a request after re-checking the liveness bound the
    /// specification carries.
    ///
    /// # Errors
    ///
    /// Returns [`RequestValidationError::InvalidDeadline`] for an already
    /// elapsed deadline and [`RequestValidationError::InvalidCancellation`]
    /// for an already cancelled signal.
    pub fn try_new(
        query: QuerySpec,
        patch: DocumentPatch,
        options: BulkByQueryOptions,
        durability: LocalDurability,
    ) -> Result<Self, RequestValidationError> {
        validate_liveness(query.deadline(), &query.cancellation())?;
        Ok(Self { query, patch, options, durability, operation_id: None })
    }

    /// Names the stable run identity: the identity the audit entries carry
    /// and every page identity derives under.
    #[must_use]
    pub fn with_operation_id(mut self, operation_id: OperationId) -> Self {
        self.operation_id = Some(operation_id);
        self
    }

    /// Returns the selection specification.
    #[must_use]
    pub const fn query(&self) -> &QuerySpec {
        &self.query
    }

    /// Returns the patch applied to every matched document.
    #[must_use]
    pub const fn patch(&self) -> &DocumentPatch {
        &self.patch
    }

    /// Returns the execution options.
    #[must_use]
    pub const fn options(&self) -> BulkByQueryOptions {
        self.options
    }

    /// Returns the durability point each page commits to.
    #[must_use]
    pub const fn durability(&self) -> LocalDurability {
        self.durability
    }

    /// Returns the stable run identity, when the caller named one.
    #[must_use]
    pub const fn operation_id(&self) -> Option<&OperationId> {
        self.operation_id.as_ref()
    }
}

/// The kind of one bulk-by-query run.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum BulkOperationKind {
    /// Delete every selected document.
    DeleteByQuery,
    /// Apply the typed patch to every selected document.
    PatchByQuery,
}

/// The receipt of one committed write page.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct BulkPageReceipt {
    operation_id: OperationId,
    ordinal: u64,
    documents: u64,
    sequence: StorageSequence,
    durability: LocalDurability,
}

impl BulkPageReceipt {
    /// Creates one page receipt from the published facts.
    #[must_use]
    pub const fn new(
        operation_id: OperationId,
        ordinal: u64,
        documents: u64,
        sequence: StorageSequence,
        durability: LocalDurability,
    ) -> Self {
        Self { operation_id, ordinal, documents, sequence, durability }
    }

    /// Returns the page transaction's operation identity: the handle
    /// [`Engine::resolve_commit`](crate::engine::Engine::resolve_commit)
    /// answers every document in this page by.
    #[must_use]
    pub const fn operation_id(&self) -> &OperationId {
        &self.operation_id
    }

    /// Returns the page's zero-based ordinal within the run.
    #[must_use]
    pub const fn ordinal(&self) -> u64 {
        self.ordinal
    }

    /// Returns the documents this page published.
    #[must_use]
    pub const fn documents(&self) -> u64 {
        self.documents
    }

    /// Returns the storage sequence the page committed at.
    #[must_use]
    pub const fn sequence(&self) -> StorageSequence {
        self.sequence
    }

    /// Returns the durability point the page reached.
    #[must_use]
    pub const fn durability(&self) -> LocalDurability {
        self.durability
    }
}

/// The per-document outcome one detail record discloses.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum BulkDetailOutcome {
    /// The document's page committed its deletion.
    Deleted,
    /// The document's page committed its patch.
    Patched,
    /// The patched value equaled the original value: nothing was staged.
    NotModified,
    /// The target vanished between selection and its page: nothing was
    /// staged for it and the run continued.
    Missing,
}

/// One bounded per-document detail record (V1's `DeleteDetails` /
/// `PatchDetails`, `BulkOperationResult.cs:86-101`, pinned V1 tree).
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct BulkDetail {
    collection: CollectionName,
    document: DocumentId,
    outcome: BulkDetailOutcome,
}

impl BulkDetail {
    /// Creates one detail record from its facts.
    #[must_use]
    pub const fn new(
        collection: CollectionName,
        document: DocumentId,
        outcome: BulkDetailOutcome,
    ) -> Self {
        Self { collection, document, outcome }
    }

    /// Returns the owning collection.
    #[must_use]
    pub const fn collection(&self) -> &CollectionName {
        &self.collection
    }

    /// Returns the document identity.
    #[must_use]
    pub const fn document(&self) -> &DocumentId {
        &self.document
    }

    /// Returns the disclosed outcome.
    #[must_use]
    pub const fn outcome(&self) -> BulkDetailOutcome {
        self.outcome
    }
}

/// The pre-execution typed refusal set of one bulk-by-query run.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum BulkRefusal {
    /// The delete-by-query request carried no confirmation token.
    ConfirmationRequired,
    /// The request shape was invalid. Reserved for surface-level request
    /// validation; construction-time refusals report through
    /// [`RequestValidationError`] directly. A family-source selection
    /// (GAP-Q19) refuses here: bulk mutations operate on documents, and
    /// counter or series mutations belong to their family batches.
    InvalidRequest,
}

/// How one bulk-by-query run ended.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum BulkTermination {
    /// The selection exhausted and every page published.
    Completed,
    /// The run refused before any data access.
    Refused(BulkRefusal),
    /// The selection query refused — including the strict-stale
    /// `IndexNotReady` refusal, the typed form of V1's stale-index throw
    /// (`AbstractDatabaseQueryRunner.cs:167-168`, pinned V1 tree).
    Selection(QueryError),
    /// The caller's cancellation was observed: published pages stay
    /// committed and the untouched identities are accounted unattempted.
    Cancelled,
    /// The deadline expired before the next selection page or write page.
    Deadline,
    /// A page commit was rejected: the run stopped, earlier pages stay
    /// committed, and the failed page's documents are accounted unattempted.
    Rejected {
        /// The failed page's operation identity.
        operation: OperationId,
        /// The typed rejection.
        error: crate::engine::CommitError,
    },
    /// A page commit's outcome is unknown: resolve by the named operation
    /// identity. The receipt claims nothing for that page.
    Unknown {
        /// The uncertain page's operation identity.
        operation: OperationId,
        /// The typed uncertainty reason.
        reason: crate::engine::UnknownReason,
    },
    /// The engine refused a snapshot, transaction, or stage.
    Engine(EngineError),
    /// A typed patch refusal other than a missing target stopped the run:
    /// the page's transaction was dropped unstaged and its documents are
    /// accounted unattempted.
    PatchRefused {
        /// The refused page's operation identity.
        operation: OperationId,
        /// The typed patch refusal.
        error: PatchError,
    },
}

/// The terminal accounting of one bulk-by-query run.
///
/// The counts reconcile exactly:
/// `selected == processed + not_modified + refused + unattempted` on every
/// path, so a run that stops anywhere never reports a document it did not
/// publish. Detail is bounded by design: the per-page receipts are O(pages)
/// — never O(documents) — and the per-document window retains at most
/// [`MAX_BULK_DETAILS`] records with the overflow counted and disclosed.
#[derive(Clone, Debug, PartialEq)]
pub struct BulkByQueryReceipt {
    kind: BulkOperationKind,
    selected: u64,
    processed: u64,
    not_modified: u64,
    refused: u64,
    unattempted: u64,
    committed_pages: u64,
    pages: Vec<BulkPageReceipt>,
    details: Vec<BulkDetail>,
    dropped_details: u64,
    staleness: StalenessReport,
    termination: BulkTermination,
}

impl BulkByQueryReceipt {
    /// Returns the kind of run this receipt accounts.
    #[must_use]
    pub const fn kind(&self) -> BulkOperationKind {
        self.kind
    }

    /// Returns the identities the selection resolved under the
    /// specification's offset and limit.
    #[must_use]
    pub const fn selected(&self) -> u64 {
        self.selected
    }

    /// Returns the identities whose page committed: deletions published,
    /// patches applied.
    #[must_use]
    pub const fn processed(&self) -> u64 {
        self.processed
    }

    /// Returns the patch-half identities whose patched value equaled the
    /// original value: V1's `NotModified` status, counted as observed but
    /// unchanged, staged nothing.
    #[must_use]
    pub const fn not_modified(&self) -> u64 {
        self.not_modified
    }

    /// Returns the per-document refusals that did not stop the run: targets
    /// that vanished between selection and their page.
    #[must_use]
    pub const fn refused(&self) -> u64 {
        self.refused
    }

    /// Returns the identities never published: a stopped run's forming page
    /// and every later page, a rejected or unknown page's staged documents,
    /// and the remainder after a cancellation or deadline stop.
    #[must_use]
    pub const fn unattempted(&self) -> u64 {
        self.unattempted
    }

    /// Returns the number of committed write pages.
    #[must_use]
    pub const fn committed_pages(&self) -> u64 {
        self.committed_pages
    }

    /// Returns the per-page receipts in commit order.
    #[must_use]
    pub fn pages(&self) -> &[BulkPageReceipt] {
        &self.pages
    }

    /// Returns the bounded per-document detail window, oldest first.
    #[must_use]
    pub fn details(&self) -> &[BulkDetail] {
        &self.details
    }

    /// Returns how many detail records were dropped from the bounded
    /// window.
    #[must_use]
    pub const fn dropped_details(&self) -> u64 {
        self.dropped_details
    }

    /// Returns the selection's per-namespace staleness report.
    #[must_use]
    pub const fn staleness(&self) -> &StalenessReport {
        &self.staleness
    }

    /// Returns the typed termination.
    #[must_use]
    pub const fn termination(&self) -> &BulkTermination {
        &self.termination
    }

    /// Reports whether the selection exhausted and every page published:
    /// the only outcome that may be read as a completed run.
    #[must_use]
    pub const fn is_complete(&self) -> bool {
        matches!(self.termination, BulkTermination::Completed)
    }

    /// Reports whether the counts reconcile.
    #[must_use]
    pub const fn reconciles(&self) -> bool {
        self.selected == self.processed + self.not_modified + self.refused + self.unattempted
    }
}

/// The lifecycle stage of one audited bulk-by-query run.
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum BulkAuditStage {
    /// The run passed its gates and began its selection — V1's scheduling
    /// position (`AbstractDatabaseOperationQueriesHandlerProcessor.cs:55-66`,
    /// pinned V1 tree).
    Dispatched,
    /// A dispatched run finished its terminal accounting, successfully or
    /// not.
    Completed,
    /// A dispatched run stopped without completing: a selection refusal, a
    /// page rejection, an unknown outcome, or a liveness stop.
    Failed,
    /// The run refused before any data access.
    Refused(BulkRefusal),
}

impl BulkAuditStage {
    /// Reports whether this is the dispatch stage.
    #[must_use]
    pub const fn is_dispatched(&self) -> bool {
        matches!(self, Self::Dispatched)
    }

    /// Reports whether this is the completion stage.
    #[must_use]
    pub const fn is_completed(&self) -> bool {
        matches!(self, Self::Completed)
    }

    /// Reports whether this is the failure stage.
    #[must_use]
    pub const fn is_failed(&self) -> bool {
        matches!(self, Self::Failed)
    }

    /// Reports whether this is the refusal stage naming `refusal`.
    #[must_use]
    pub fn is_refused(&self, refusal: &BulkRefusal) -> bool {
        matches!(self, Self::Refused(named) if named == refusal)
    }
}

/// One typed, content-free bulk-by-query audit event.
///
/// The event names only structural identity and counts: kind, tenant, run
/// identity, stage, selected, processed. It structurally cannot carry a
/// query text, document body, or field — V1's audit line carries the query
/// (`AbstractDatabaseOperationQueriesHandlerProcessor.cs:65`, pinned V1
/// tree); the engine journal is content-free by the seam's audit
/// discipline, recorded as an accepted difference in the decision record.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct BulkAuditEntry {
    sequence: u64,
    stage: BulkAuditStage,
    kind: BulkOperationKind,
    tenant: TenantId,
    operation: Option<OperationId>,
    selected: u64,
    processed: u64,
}

impl BulkAuditEntry {
    /// Creates one event from its structural identity parts.
    #[must_use]
    pub const fn new(
        sequence: u64,
        stage: BulkAuditStage,
        kind: BulkOperationKind,
        tenant: TenantId,
        operation: Option<OperationId>,
        selected: u64,
        processed: u64,
    ) -> Self {
        Self { sequence, stage, kind, tenant, operation, selected, processed }
    }

    /// Returns the monotonic journal sequence of this event.
    #[must_use]
    pub const fn sequence(&self) -> u64 {
        self.sequence
    }

    /// Returns the lifecycle stage of the audited run.
    #[must_use]
    pub const fn stage(&self) -> &BulkAuditStage {
        &self.stage
    }

    /// Returns the kind of the audited run.
    #[must_use]
    pub const fn kind(&self) -> BulkOperationKind {
        self.kind
    }

    /// Returns the tenant the run executed under.
    #[must_use]
    pub const fn tenant(&self) -> &TenantId {
        &self.tenant
    }

    /// Returns the run identity the audit carries.
    #[must_use]
    pub const fn operation(&self) -> Option<&OperationId> {
        self.operation.as_ref()
    }

    /// Returns the identities the run selected.
    #[must_use]
    pub const fn selected(&self) -> u64 {
        self.selected
    }

    /// Returns the identities the run published.
    #[must_use]
    pub const fn processed(&self) -> u64 {
        self.processed
    }
}

/// The bounded in-process journal of bulk-by-query audit events.
///
/// Recording takes the journal lock only: no Engine lock, no storage lock,
/// no I/O, so a run is never blocked by audit recording. Entries beyond
/// [`MAX_BULK_AUDIT_ENTRIES`] drop the oldest; durable audit shipping
/// belongs to the Platform side, exactly as the adapter audit skeleton
/// records.
pub(crate) struct BulkAuditJournal {
    next_sequence: AtomicU64,
    events: Mutex<Vec<BulkAuditEntry>>,
}

impl BulkAuditJournal {
    /// Creates an empty journal.
    pub(crate) fn new() -> Self {
        Self { next_sequence: AtomicU64::new(1), events: Mutex::new(Vec::new()) }
    }

    /// Records one event, dropping the oldest beyond the bound.
    pub(crate) fn record(
        &self,
        stage: BulkAuditStage,
        kind: BulkOperationKind,
        tenant: &TenantId,
        operation: Option<&OperationId>,
        selected: u64,
        processed: u64,
    ) {
        let sequence = self.next_sequence.fetch_add(1, Ordering::Relaxed);
        let event = BulkAuditEntry::new(
            sequence,
            stage,
            kind,
            tenant.clone(),
            operation.cloned(),
            selected,
            processed,
        );
        if let Ok(mut events) = self.events.lock() {
            events.push(event);
            let excess = events.len().saturating_sub(MAX_BULK_AUDIT_ENTRIES);
            if excess > 0 {
                events.drain(0..excess);
            }
        }
        // A poisoned journal lock drops the event rather than panicking the
        // run; the sequence keeps advancing so the gap is visible.
    }

    /// Returns the retained events in recorded order.
    pub(crate) fn entries(&self) -> Vec<BulkAuditEntry> {
        self.events.lock().map(|events| events.clone()).unwrap_or_default()
    }
}

/// The per-run execution parts shared by both halves.
struct BulkRun {
    kind: BulkOperationKind,
    query: QuerySpec,
    patch: Option<DocumentPatch>,
    options: BulkByQueryOptions,
    durability: LocalDurability,
    operation_id: Option<OperationId>,
    confirmation: Option<DeletionConfirmation>,
}

impl BulkRun {
    /// Returns the run identity the audit entries carry: the caller's
    /// stable identity when named, a minted one otherwise.
    fn run_identity(&self) -> OperationId {
        match &self.operation_id {
            Some(named) => named.clone(),
            None => OperationId::try_new(&format!("tbv2-bulkq-{}", next_run_identity()))
                .expect("derived run identities are valid"),
        }
    }
}

/// Re-checks the liveness bound a request carries.
fn validate_liveness(
    deadline: Deadline,
    cancellation: &Cancellation,
) -> Result<(), RequestValidationError> {
    if deadline.is_expired() {
        return Err(RequestValidationError::InvalidDeadline);
    }
    if cancellation.is_cancelled() {
        return Err(RequestValidationError::InvalidCancellation);
    }
    Ok(())
}

impl QueryStore<'_> {
    /// Deletes every document the selection matches, page-wise, after the
    /// explicit confirmation token.
    ///
    /// The run compiles the request's specification as the selection,
    /// resolves the matched identities against one immutable snapshot, and
    /// deletes them in bounded pages — one atomic transaction per page. A
    /// request without a confirmation token refuses before any data access
    /// with [`BulkRefusal::ConfirmationRequired`]. The decision record is
    /// `docs/query/gapq10-bulk-by-query.md`.
    ///
    /// The cancellation signal is taken by reference because a cloned
    /// [`Cancellation`] is an independent snapshot: a by-value signal could
    /// never be cancelled mid-run.
    #[must_use]
    pub fn delete_by_query(
        &self,
        scope: &TenantScope,
        request: DeleteByQueryRequest,
        cancellation: &Cancellation,
    ) -> BulkByQueryReceipt {
        let confirmation = request.confirmation;
        let run = BulkRun {
            kind: BulkOperationKind::DeleteByQuery,
            query: request.query,
            patch: None,
            options: request.options,
            durability: request.durability,
            operation_id: request.operation_id,
            confirmation,
        };
        self.run_bulk(scope, &run, cancellation)
    }

    /// Applies the typed patch to every document the selection matches,
    /// page-wise.
    ///
    /// The patch is the GAP-D07 [`DocumentPatch`] vocabulary applied per
    /// matched document under [`ExpectedVersion::Any`] — V1's patch-by-query
    /// command shape with a null change vector. A missing target is a
    /// recorded status, not an error (V1's `PatchStatus.DocumentDoesNotExist`);
    /// any other typed patch refusal stops the run.
    ///
    /// The cancellation signal is taken by reference because a cloned
    /// [`Cancellation`] is an independent snapshot.
    #[must_use]
    pub fn patch_by_query(
        &self,
        scope: &TenantScope,
        request: PatchByQueryRequest,
        cancellation: &Cancellation,
    ) -> BulkByQueryReceipt {
        let run = BulkRun {
            kind: BulkOperationKind::PatchByQuery,
            query: request.query,
            patch: Some(request.patch),
            options: request.options,
            durability: request.durability,
            operation_id: request.operation_id,
            confirmation: None,
        };
        self.run_bulk(scope, &run, cancellation)
    }

    /// Executes one bulk run: gates, page-wise selection, page-wise writes,
    /// audit, and the reconciling receipt.
    fn run_bulk(
        &self,
        scope: &TenantScope,
        run: &BulkRun,
        cancellation: &Cancellation,
    ) -> BulkByQueryReceipt {
        let mut receipt = BulkByQueryReceipt {
            kind: run.kind,
            selected: 0,
            processed: 0,
            not_modified: 0,
            refused: 0,
            unattempted: 0,
            committed_pages: 0,
            pages: Vec::new(),
            details: Vec::new(),
            dropped_details: 0,
            staleness: StalenessReport::default(),
            termination: BulkTermination::Completed,
        };
        let run_identity = run.run_identity();
        let audit = |stage: BulkAuditStage, selected: u64, processed: u64| {
            self.engine.record_bulk_audit(
                stage,
                run.kind,
                scope.tenant(),
                Some(&run_identity),
                selected,
                processed,
            );
        };

        // Gate 1 — confirmation: the delete half refuses without the token,
        // before any data access, with a Refused audit entry and no dispatch.
        if run.kind == BulkOperationKind::DeleteByQuery && run.confirmation.is_none() {
            receipt.termination = BulkTermination::Refused(BulkRefusal::ConfirmationRequired);
            audit(BulkAuditStage::Refused(BulkRefusal::ConfirmationRequired), 0, 0);
            return receipt;
        }
        // Gate 1b — source kind: a family source names no documents, so a
        // page-wise document mutation has nothing to select (GAP-Q19).
        // Counter and series mutations go through their family batches.
        if run.query.source().kind() != super::spec::QuerySourceKind::Collection {
            receipt.termination = BulkTermination::Refused(BulkRefusal::InvalidRequest);
            audit(BulkAuditStage::Refused(BulkRefusal::InvalidRequest), 0, 0);
            return receipt;
        }
        // Gate 2 — liveness: a pre-cancelled or pre-expired run never
        // dispatches; the counts stay zero and the stop is audited.
        if cancellation.is_cancelled() {
            receipt.termination = BulkTermination::Cancelled;
            audit(BulkAuditStage::Failed, 0, 0);
            return receipt;
        }
        if run.query.deadline().is_expired() {
            receipt.termination = BulkTermination::Deadline;
            audit(BulkAuditStage::Failed, 0, 0);
            return receipt;
        }
        audit(BulkAuditStage::Dispatched, 0, 0);

        // Selection: resolve the matched identities against one immutable
        // snapshot, in pages bounded by the query seam's own page limit and
        // capped by the specification's limit (V1's PageSize).
        let selection = match self.select_identities(run, scope, cancellation) {
            Ok(selection) => selection,
            Err(termination) => {
                receipt.termination = termination;
                audit(BulkAuditStage::Failed, 0, 0);
                return receipt;
            }
        };
        let Selection { identities, staleness } = selection;
        receipt.staleness = staleness;
        receipt.selected = u64::try_from(identities.len()).unwrap_or(u64::MAX);

        // Writes: one atomic transaction per bounded page.
        let stopped =
            self.publish_pages(run, scope, &run_identity, &identities, cancellation, &mut receipt);
        if !stopped {
            receipt.termination = BulkTermination::Completed;
        }
        // The unattempted count is the exact slack: every identity lands in
        // exactly one bucket of the reconciling identity.
        receipt.unattempted = receipt
            .selected
            .saturating_sub(receipt.processed + receipt.not_modified + receipt.refused);
        let stage =
            if receipt.is_complete() { BulkAuditStage::Completed } else { BulkAuditStage::Failed };
        audit(stage, receipt.selected, receipt.processed);
        receipt
    }

    /// Resolves the selection's matched identities page-wise against one
    /// immutable snapshot.
    ///
    /// The snapshot cannot shift under the selection, so advancing the page
    /// offset over the same snapshot is sound. The identities are the only
    /// thing the selection materializes — V1's id-only operation projection
    /// (`AbstractDatabaseQueryRunner.cs:225`, pinned V1 tree).
    fn select_identities(
        &self,
        run: &BulkRun,
        scope: &TenantScope,
        cancellation: &Cancellation,
    ) -> Result<Selection, BulkTermination> {
        let snapshot = self.engine.read_snapshot(scope).map_err(BulkTermination::Engine)?;
        let mut identities: Vec<DocumentId> = Vec::new();
        let mut staleness = StalenessReport::default();
        let mut cursor = run.query.paging().offset();
        loop {
            if let Err(error) = check_bounds(Some(run.query.deadline()), Some(cancellation)) {
                return Err(selection_liveness(error));
            }
            let remaining = run.query.paging().limit().map_or(MAX_PAGE_LIMIT, |limit| {
                limit.saturating_sub(u32::try_from(identities.len()).unwrap_or(u32::MAX))
            });
            if remaining == 0 {
                break;
            }
            let page_limit = remaining.min(MAX_PAGE_LIMIT);
            let paged = run.query.clone().with_paging(
                QueryPaging::try_new(cursor, Some(page_limit)).expect("in-bounds page"),
            );
            let plan =
                self.compile(QueryInput::typed(paged)).map_err(BulkTermination::Selection)?;
            let result = self.execute(&plan, &snapshot).map_err(BulkTermination::Selection)?;
            staleness = result.staleness().clone();
            let rows = result.rows().len();
            for row in result.rows() {
                identities.push(row.document().id().clone());
            }
            let page_width = usize::try_from(page_limit).unwrap_or(usize::MAX);
            if rows < page_width {
                break;
            }
            cursor = cursor.saturating_add(u64::try_from(rows).unwrap_or(u64::MAX));
            if run.query.paging().limit().is_some_and(|limit| {
                identities.len() >= usize::try_from(limit).unwrap_or(usize::MAX)
            }) {
                break;
            }
        }
        Ok(Selection { identities, staleness })
    }

    /// Publishes the selected identities in bounded write pages.
    ///
    /// Returns whether the run stopped before exhausting the identities;
    /// the receipt already carries the termination and the exact counts of
    /// every committed page on either path.
    fn publish_pages(
        &self,
        run: &BulkRun,
        scope: &TenantScope,
        run_identity: &OperationId,
        identities: &[DocumentId],
        cancellation: &Cancellation,
        receipt: &mut BulkByQueryReceipt,
    ) -> bool {
        let Some(collection) = run.query.source().collection().cloned() else {
            // Unreachable through the public path — the family-source gate
            // refuses before selection — but a document mutation without a
            // document source still stops here instead of guessing.
            receipt.termination = BulkTermination::Refused(BulkRefusal::InvalidRequest);
            return true;
        };
        let mut next_page: u64 = 0;
        let mut index = 0usize;
        while index < identities.len() {
            // Liveness gates between pages: a stop leaves earlier pages
            // committed and the untouched identities unattempted.
            if cancellation.is_cancelled() {
                receipt.termination = BulkTermination::Cancelled;
                return true;
            }
            if run.query.deadline().is_expired() {
                receipt.termination = BulkTermination::Deadline;
                return true;
            }
            let end = (index + run.options.page_documents()).min(identities.len());
            let (operation, ordinal) = page_operation_id(run_identity, &mut next_page);
            let snapshot = match self.engine.read_snapshot(scope) {
                Ok(snapshot) => snapshot,
                Err(error) => {
                    receipt.termination = BulkTermination::Engine(error);
                    return true;
                }
            };
            let mut transaction = match self.engine.begin(scope, snapshot, operation.clone()) {
                Ok(transaction) => transaction,
                Err(error) => {
                    receipt.termination = BulkTermination::Engine(error);
                    return true;
                }
            };
            let staged = match stage_page(
                &mut transaction,
                run,
                &collection,
                &identities[index..end],
                &operation,
                receipt,
            ) {
                Ok(staged) => staged,
                Err(stop) => {
                    receipt.termination = match stop {
                        PageStop::Engine(error) => BulkTermination::Engine(error),
                        PageStop::Patch { operation, error } => {
                            BulkTermination::PatchRefused { operation, error }
                        }
                    };
                    return true;
                }
            };
            let documents = u64::try_from(staged.len()).unwrap_or(u64::MAX);
            if staged.is_empty() {
                // A page whose every document was not-modified or refused
                // stages nothing: there is no transaction to publish — the
                // commit seam refuses an empty write set — so the page ends
                // unstaged and the run continues. Its observations are
                // already accounted (`not_modified`, `refused`).
                index = end;
                continue;
            }
            match transaction.commit(run.durability) {
                CommitOutcome::Committed(commit) => {
                    receipt.processed += documents;
                    receipt.committed_pages += 1;
                    receipt.pages.push(BulkPageReceipt::new(
                        operation,
                        ordinal,
                        documents,
                        commit.sequence(),
                        commit.durability(),
                    ));
                    if run.options.details() {
                        for identity in &staged {
                            let outcome = match run.kind {
                                BulkOperationKind::DeleteByQuery => BulkDetailOutcome::Deleted,
                                BulkOperationKind::PatchByQuery => BulkDetailOutcome::Patched,
                            };
                            record_detail(receipt, true, &collection, identity, outcome);
                        }
                    }
                }
                CommitOutcome::Rejected(error) => {
                    receipt.termination = BulkTermination::Rejected { operation, error };
                    return true;
                }
                CommitOutcome::Unknown(unknown) => {
                    receipt.termination =
                        BulkTermination::Unknown { operation, reason: unknown.reason() };
                    return true;
                }
            }
            index = end;
        }
        false
    }
}

/// How one write page's staging ended before its documents were staged.
enum PageStop {
    /// The engine refused a read inside the page.
    Engine(EngineError),
    /// A typed patch refusal: the page's transaction is dropped unstaged and
    /// the run stops, naming the page's operation identity.
    Patch {
        /// The refused page's operation identity.
        operation: OperationId,
        /// The typed patch refusal.
        error: PatchError,
    },
}

/// Stages one page's documents into the page transaction.
///
/// Per-document observations are counted into the receipt as they happen:
/// a vanished target refuses without staging (and the page continues), and
/// a not-modified patch observes without staging. The staged identities are
/// returned for the page's commit-time accounting and detail disclosure —
/// a staged document is disclosed only when its page commits.
fn stage_page(
    transaction: &mut crate::engine::Transaction<'_>,
    run: &BulkRun,
    collection: &CollectionName,
    page: &[DocumentId],
    operation: &OperationId,
    receipt: &mut BulkByQueryReceipt,
) -> Result<Vec<DocumentId>, PageStop> {
    let mut staged: Vec<DocumentId> = Vec::new();
    for identity in page {
        match run.kind {
            BulkOperationKind::DeleteByQuery => {
                match transaction.read_in_collection(collection, identity) {
                    Ok(None) => {
                        // A vanished target stages nothing — no phantom
                        // tombstone — and the run continues.
                        receipt.refused += 1;
                        record_detail(
                            receipt,
                            run.options.details(),
                            collection,
                            identity,
                            BulkDetailOutcome::Missing,
                        );
                    }
                    Ok(Some(_)) => {
                        transaction.delete_in_collection(
                            collection,
                            identity.clone(),
                            ExpectedVersion::Any,
                        );
                        staged.push(identity.clone());
                    }
                    Err(error) => return Err(PageStop::Engine(error)),
                }
            }
            BulkOperationKind::PatchByQuery => {
                let patch = run.patch.as_ref().expect("patch runs carry the patch");
                match apply_document_patch(
                    transaction,
                    collection,
                    identity,
                    ExpectedVersion::Any,
                    patch,
                ) {
                    // `Created` is unreachable through a `DocumentPatch`
                    // (only `AddOrPatch` creates); accounted as applied if a
                    // future compound reaches this seam.
                    Ok(PatchOutcome::Applied | PatchOutcome::Created) => {
                        staged.push(identity.clone());
                    }
                    Ok(PatchOutcome::NotModified) => {
                        receipt.not_modified += 1;
                        record_detail(
                            receipt,
                            run.options.details(),
                            collection,
                            identity,
                            BulkDetailOutcome::NotModified,
                        );
                    }
                    // A missing target is a recorded status, not an error:
                    // V1's `PatchStatus.DocumentDoesNotExist`.
                    Err(PatchError::MissingTarget) => {
                        receipt.refused += 1;
                        record_detail(
                            receipt,
                            run.options.details(),
                            collection,
                            identity,
                            BulkDetailOutcome::Missing,
                        );
                    }
                    Err(error) => {
                        return Err(PageStop::Patch { operation: operation.clone(), error });
                    }
                }
            }
        }
    }
    Ok(staged)
}

/// The resolved selection of one bulk run.
struct Selection {
    identities: Vec<DocumentId>,
    staleness: StalenessReport,
}

/// Records one per-document detail into the bounded window.
fn record_detail(
    receipt: &mut BulkByQueryReceipt,
    details: bool,
    collection: &CollectionName,
    identity: &DocumentId,
    outcome: BulkDetailOutcome,
) {
    if !details {
        return;
    }
    if receipt.details.len() >= MAX_BULK_DETAILS {
        receipt.details.remove(0);
        receipt.dropped_details += 1;
    }
    receipt.details.push(BulkDetail::new(collection.clone(), identity.clone(), outcome));
}

/// Maps a selection-loop liveness failure onto its termination.
fn selection_liveness(error: QueryError) -> BulkTermination {
    match error {
        QueryError::Cancelled => BulkTermination::Cancelled,
        QueryError::Deadline => BulkTermination::Deadline,
        other => BulkTermination::Selection(other),
    }
}

/// Mints never-reused run identities for the derived page identities.
fn next_run_identity() -> u64 {
    static COUNTER: AtomicU64 = AtomicU64::new(0);
    COUNTER.fetch_add(1, Ordering::Relaxed)
}

/// Derives the next page's operation identity: one identity per page
/// transaction, the engine's `OperationId` discipline.
fn page_operation_id(run_identity: &OperationId, next_page: &mut u64) -> (OperationId, u64) {
    let ordinal = *next_page;
    *next_page = next_page.saturating_add(1);
    let operation = OperationId::try_new(&format!("{}-p{ordinal}", run_identity.as_str()))
        .expect("derived page identities are valid");
    (operation, ordinal)
}
