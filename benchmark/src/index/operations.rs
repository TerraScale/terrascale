//! The typed index lifecycle operations, state, staleness, and statistics
//! of the engine seam (GAP-Q18).
//!
//! This module owns the administrative half of index lifecycle in V2:
//! the per-index operational state machine (create, delete, start, stop,
//! enable, disable, reset with in-place and side-by-side modes), lock
//! modes and priority on the deployment record, the recorded-error
//! contract, per-state staleness, the bounded wait-for-non-stale, and the
//! engine-owned statistics surface. The decision record — the state
//! matrix, the staleness contract, the wait semantics, the statistics
//! inventory, and the deferred items — is
//! `docs/index/gapq18-lifecycle-staleness.md`.
//!
//! The automatic lifecycle of optimizer-created indexes (IX-03) is
//! untouched: every operation here applies to explicitly created indexes
//! only and refuses an `Auto/` namespace fail-closed, so no administrative
//! transition ever fights the optimizer's own state machine. There is no
//! authorization surface here by design — the authorization class of the
//! admin operations resolves at the Platform Adapter (charter
//! PD-2026-08-26-11, PB-4062).
//!
//! V1 anchors cite the pinned read-only tree recorded in the decision
//! document.

use std::{sync::Arc, time::Duration, time::Instant};

use super::{IndexBuilder, IndexCatalog, RebuildOutcome};
use crate::{
    engine::Engine,
    query::{
        IndexNamespace,
        optimizer::{IndexDefinition, IndexKind},
    },
    types::TenantId,
};

/// The maximum number of recorded failure records one index keeps.
///
/// The newest records are retained; older ones are dropped as newer
/// failures arrive, so the observable error history of one index is
/// bounded engine state.
pub const MAX_INDEX_ERRORS_PER_INDEX: usize = 64;

/// The number of consecutive failed build or publish attempts that moves
/// an index into the [`Error`](IndexOperationalState::Error) state.
///
/// The typed form of V1's error-limit rule: a failure count past the
/// limit calls `SetState(IndexState.Error)`. It is an initial engine
/// bound, tunable with evidence, never a performance claim.
pub const INDEX_ERROR_STATE_THRESHOLD: usize = 8;

/// The operational state of one explicitly created index.
///
/// This is V1's `IndexState` folded to one axis (V1 carries `State` and a
/// separate running status): `Normal` maps V1's `Normal`, `Paused` maps
/// the node-local stop of `StopIndex`, `Disabled` maps the persistent
/// stop of `DisableIndex`, and `Error` maps the failure-entered state.
/// V1's `Idle` lives in the automatic lifecycle of auto indexes
/// (`IndexLifecycleState::Idle`, IX-03) — demotion in preference, not an
/// operational condition.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum IndexOperationalState {
    /// Serving and indexing normally.
    Normal,
    /// Paused: performs no indexing, still queryable; results may be
    /// stale while coverage trails (V1 `StopIndexOperation.cs:9-13`).
    Paused,
    /// Disabled: the persistent form of inertness, intended to survive a
    /// restart in V1; still queryable with the same staleness rule (V1
    /// `DisableIndexOperation.cs:8-14`).
    Disabled,
    /// Error: entered by repeated build or publish failures; unconditionally
    /// stale until cleared (V1 `Index.cs:2196-2218`, `:1373`).
    Error,
}

impl IndexOperationalState {
    /// Returns the canonical state name.
    #[must_use]
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::Normal => "Normal",
            Self::Paused => "Paused",
            Self::Disabled => "Disabled",
            Self::Error => "Error",
        }
    }
}

/// The lifecycle operation a transition request named.
///
/// The refusal error carries this, so an illegal transition names both
/// the state it found and the operation it refused — never a document
/// value.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum IndexLifecycleOperation {
    /// Resume from pause (`StartIndex`).
    Start,
    /// Pause (`StopIndex`).
    Stop,
    /// Clear a persistent disable (`EnableIndex`).
    Enable,
    /// Persistently disable (`DisableIndex`).
    Disable,
    /// Drop indexed data and re-index (`ResetIndex`).
    Reset,
    /// Clear recorded errors and leave the error state.
    ClearErrors,
}

impl IndexLifecycleOperation {
    /// Returns the canonical operation name.
    #[must_use]
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::Start => "start",
            Self::Stop => "stop",
            Self::Enable => "enable",
            Self::Disable => "disable",
            Self::Reset => "reset",
            Self::ClearErrors => "clear-errors",
        }
    }
}

/// Typed failures of the index lifecycle operations.
///
/// Every variant names the violated contract rule; none carries document
/// data, field values, or paths.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum IndexOperationError {
    /// The namespace has no registered definition in this catalog.
    NamespaceUnknown,
    /// The namespace is not an explicitly created index: automatic
    /// indexes own their lifecycle to the optimizer, so every
    /// administrative operation refuses them fail-closed.
    NotExplicitIndex,
    /// The requested transition is illegal from the state the index is
    /// in; the state and the operation are named, and nothing changed.
    IllegalTransition {
        /// The state the index was in.
        from: IndexOperationalState,
        /// The operation that was refused.
        operation: IndexLifecycleOperation,
    },
    /// A definition change was refused because the index is locked in the
    /// error mode (V1 `IndexLockMode.LockedError`).
    DefinitionLocked,
    /// The namespace squats the reserved engine-owned auto namespace,
    /// which explicit creation never occupies.
    AutoNamespaceReserved,
    /// The definition's kind is not explicit.
    KindMismatch,
}

/// The deployment options an explicit index carries at creation.
///
/// Priority is observable metadata (V1 `IndexPriority.cs:29-33`); the
/// in-memory catalog has no indexing thread pool to order, so it carries
/// no scheduling semantics here. The lock mode guards definition changes
/// exactly as V1's does (V1 `IndexLockMode.cs:22-27`).
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct IndexDeployment {
    priority: IndexPriority,
    lock_mode: IndexLockMode,
}

impl IndexDeployment {
    /// The default deployment: normal priority, unlocked.
    pub const DEFAULT: Self =
        Self { priority: IndexPriority::Normal, lock_mode: IndexLockMode::Unlock };

    /// Creates one deployment record from its options.
    #[must_use]
    pub const fn new(priority: IndexPriority, lock_mode: IndexLockMode) -> Self {
        Self { priority, lock_mode }
    }

    /// Returns the deployment priority.
    #[must_use]
    pub const fn priority(self) -> IndexPriority {
        self.priority
    }

    /// Returns the definition-change lock mode.
    #[must_use]
    pub const fn lock_mode(self) -> IndexLockMode {
        self.lock_mode
    }
}

/// The processing priority recorded on one explicit index.
///
/// V1 `IndexPriority.cs:29-33`. Observable deployment metadata; the
/// scheduling effect belongs to a future indexing scheduler owned by the
/// IX wave.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum IndexPriority {
    /// Lower processing priority than normal.
    Low,
    /// The default priority.
    Normal,
    /// Higher processing priority than normal.
    High,
}

/// How an index's definition is guarded against changes.
///
/// V1 `IndexLockMode.cs:22-27`: unlock accepts changes, locked-ignore
/// accepts them as a recorded no-op, and locked-error refuses them.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum IndexLockMode {
    /// All definition changes are accepted.
    Unlock,
    /// Definition changes are silently ignored: the call succeeds as a
    /// recorded no-op and the stored definition stands.
    LockedIgnore,
    /// Definition changes fail closed with
    /// [`IndexOperationError::DefinitionLocked`].
    LockedError,
}

/// The reset mode of [`IndexStore::reset`].
///
/// V1 `ResetIndexOperation.cs:44-46`.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum IndexResetMode {
    /// Removes all existing indexed data and re-indexes in place: the
    /// epoch is kept, published segments and publication history are
    /// dropped, and generation numbering restarts.
    InPlace,
    /// Builds a replacement in the next epoch while the old epoch keeps
    /// serving; the first publication of the new epoch is the cutover.
    SideBySide,
}

/// The outcome of one state transition.
///
/// `changed` is false exactly when the operation was a legal no-op — the
/// idempotent same-axis call V1 guards with an early return.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct LifecycleTransition {
    from: IndexOperationalState,
    to: IndexOperationalState,
    changed: bool,
}

impl LifecycleTransition {
    /// Creates one transition record. Crate-only: the catalog is the only
    /// producer.
    pub(crate) const fn new(
        from: IndexOperationalState,
        to: IndexOperationalState,
        changed: bool,
    ) -> Self {
        Self { from, to, changed }
    }

    /// Returns the state the index was in.
    #[must_use]
    pub const fn from(&self) -> IndexOperationalState {
        self.from
    }

    /// Returns the state the index moved to.
    #[must_use]
    pub const fn to(&self) -> IndexOperationalState {
        self.to
    }

    /// Reports whether the transition changed anything. A reset reports a
    /// change even when the state stands, because the indexed data was
    /// dropped.
    #[must_use]
    pub const fn changed(&self) -> bool {
        self.changed
    }
}

/// The outcome of one explicit-index creation.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum IndexCreationOutcome {
    /// The namespace was created fresh at the epoch.
    Created {
        /// The epoch the new namespace starts in.
        epoch: u64,
    },
    /// A different definition replaced the stored one under an unlocked
    /// index.
    Updated {
        /// The namespace's preserved epoch.
        epoch: u64,
    },
    /// The same definition was re-registered; nothing changed.
    Unchanged {
        /// The namespace's current epoch.
        epoch: u64,
    },
    /// The definition change was ignored under the locked-ignore mode.
    Ignored {
        /// The namespace's current epoch.
        epoch: u64,
    },
}

impl IndexCreationOutcome {
    /// Returns the namespace's epoch after the operation.
    #[must_use]
    pub const fn epoch(self) -> u64 {
        match self {
            Self::Created { epoch }
            | Self::Updated { epoch }
            | Self::Unchanged { epoch }
            | Self::Ignored { epoch } => epoch,
        }
    }
}

/// The engine action a recorded failure happened in.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum IndexErrorAction {
    /// A segment build failed.
    Build,
    /// A segment publication failed.
    Publish,
}

/// The typed failure category of one recorded error.
///
/// Drawn from the crate's existing index failure vocabulary; no category
/// carries document data.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum IndexErrorCategory {
    /// The Engine's snapshot view failed.
    StorageFailure,
    /// The generation sequence of the namespace's epoch is exhausted.
    GenerationExhausted,
}

impl IndexErrorCategory {
    /// Maps one internal index failure onto its recorded category.
    ///
    /// Caller-contract failures (unknown namespace, replayed events) are
    /// pre-checked by the maintenance step and cannot reach the record;
    /// they map conservatively to the storage category so the mapping is
    /// total without fabricating a reachable category for them.
    pub(crate) const fn of(failure: super::IndexError) -> Self {
        match failure {
            super::IndexError::GenerationExhausted => Self::GenerationExhausted,
            super::IndexError::StorageFailure
            | super::IndexError::NamespaceUnknown
            | super::IndexError::EpochNotNewer
            | super::IndexError::NoPublicationObserved
            | super::IndexError::AutoNamespaceReserved
            | super::IndexError::KindMismatch
            | super::IndexError::AutoIdentityMismatch
            | super::IndexError::AutoRegistryFull => Self::StorageFailure,
        }
    }
}

/// One recorded index failure: content-safe by construction.
///
/// The record names the action and the typed failure category with the
/// lifecycle-clock instant it was observed — never an error string, a
/// document value, or a path (V1's error surfaces carry free-text
/// messages; V2's recorded form is typed and content-free).
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct IndexErrorRecord {
    action: IndexErrorAction,
    category: IndexErrorCategory,
    at: Instant,
}

impl IndexErrorRecord {
    /// Creates one record. Crate-only: the engine's failure ingress is
    /// the only producer.
    pub(crate) const fn new(
        action: IndexErrorAction,
        category: IndexErrorCategory,
        at: Instant,
    ) -> Self {
        Self { action, category, at }
    }

    /// Returns the engine action that failed.
    #[must_use]
    pub const fn action(&self) -> IndexErrorAction {
        self.action
    }

    /// Returns the typed failure category.
    #[must_use]
    pub const fn category(&self) -> IndexErrorCategory {
        self.category
    }

    /// Returns the lifecycle-clock instant the failure was recorded.
    #[must_use]
    pub const fn at(&self) -> Instant {
        self.at
    }
}

/// One index's recorded errors as the typed listing reports them.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct IndexErrorListing {
    name: String,
    records: Vec<IndexErrorRecord>,
}

impl IndexErrorListing {
    /// Creates one listing. Crate-only: the catalog is the only producer.
    pub(crate) fn new(name: String, records: Vec<IndexErrorRecord>) -> Self {
        Self { name, records }
    }

    /// Returns the catalog name of the index the records belong to.
    #[must_use]
    pub fn name(&self) -> &str {
        &self.name
    }

    /// Returns the bounded recorded failures, oldest first.
    #[must_use]
    pub fn records(&self) -> &[IndexErrorRecord] {
        &self.records
    }
}

/// One typed reason an index is stale.
///
/// The typed form of V1's `stalenessReasons` list (V1
/// `Index.cs:1362-1376`).
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum StalenessReason {
    /// The index is in the error state, which is unconditionally stale
    /// (V1's faulty-index rule, `Index.cs:1373`).
    ErrorState,
    /// The index's covered storage sequence trails the latest observed
    /// publication.
    CoverageBehind {
        /// The sequence the index covers.
        covered: u64,
        /// The latest observed publication sequence.
        latest: u64,
    },
    /// Publications exist but the index has never published a segment.
    NotYetPublished {
        /// The latest observed publication sequence.
        latest: u64,
    },
}

/// The staleness of one index: its operational state, whether it is
/// stale, and the typed reasons.
///
/// Deterministic by construction: a pure function of guarded catalog
/// state, no clock input.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct IndexStaleness {
    state: IndexOperationalState,
    stale: bool,
    reasons: Vec<StalenessReason>,
}

impl IndexStaleness {
    /// Creates one staleness report. Crate-only: the catalog is the only
    /// producer.
    pub(crate) const fn new(
        state: IndexOperationalState,
        is_stale: bool,
        reasons: Vec<StalenessReason>,
    ) -> Self {
        Self { state, stale: is_stale, reasons }
    }

    /// Returns the operational state the staleness was evaluated against.
    #[must_use]
    pub const fn state(&self) -> IndexOperationalState {
        self.state
    }

    /// Reports whether the index is stale.
    #[must_use]
    pub const fn stale(&self) -> bool {
        self.stale
    }

    /// Returns the typed staleness reasons, in evaluation order.
    #[must_use]
    pub fn reasons(&self) -> &[StalenessReason] {
        &self.reasons
    }
}

/// The bound of one wait-for-non-stale call.
///
/// The default is V1's 15-second convention (V1
/// `DocumentConventions.cs:91`, `DynamicQueryRunner.cs:191-194`). A zero
/// bound is legal and means "evaluate once, never step". The bound is a
/// deadline, not a sleep: see [`IndexStore::wait_for_non_stale`].
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct WaitForNonStale(Duration);

impl WaitForNonStale {
    /// The default bound: fifteen seconds, V1's convention.
    pub const DEFAULT: Self = Self(Duration::from_secs(15));

    /// Creates one bounded wait.
    #[must_use]
    pub const fn bounded(timeout: Duration) -> Self {
        Self(timeout)
    }

    /// Returns the deadline bound.
    #[must_use]
    pub const fn timeout(self) -> Duration {
        self.0
    }
}

impl Default for WaitForNonStale {
    fn default() -> Self {
        Self::DEFAULT
    }
}

/// The outcome of one bounded wait-for-non-stale call.
///
/// The engine returns the typed outcome with the final staleness it
/// observed; translating a timeout into a caller-facing error belongs to
/// the caller, exactly as V1's client — not its server — throws the
/// timeout (V1 `QueryOperation.cs:453-465`).
#[derive(Clone, Debug, Eq, PartialEq)]
pub enum NonStaleWaitOutcome {
    /// The index was (or became) non-stale; the carried report is fresh.
    NonStale(IndexStaleness),
    /// The bound hit, or no progress was possible; the carried report is
    /// the staleness the wait ended on.
    TimedOut(IndexStaleness),
}

impl NonStaleWaitOutcome {
    /// Reports whether the wait ended non-stale.
    #[must_use]
    pub const fn is_non_stale(&self) -> bool {
        matches!(self, Self::NonStale(_))
    }

    /// Returns the final staleness the wait observed.
    #[must_use]
    pub const fn staleness(&self) -> &IndexStaleness {
        match self {
            Self::NonStale(report) | Self::TimedOut(report) => report,
        }
    }
}

/// The outcome of one bounded rebuild step.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum RebuildStepOutcome {
    /// The step published a segment at a new generation.
    Published {
        /// The epoch published into.
        epoch: u64,
        /// The generation published.
        generation: u64,
        /// The storage sequence the segment covers.
        covered_sequence: u64,
    },
    /// There was nothing to build: no observed publication outruns the
    /// index's coverage.
    UpToDate,
    /// The step was skipped because the index's operational state is not
    /// [`Normal`](IndexOperationalState::Normal); paused, disabled, and
    /// error indexes perform no indexing.
    Skipped {
        /// The state that refused the step.
        state: IndexOperationalState,
    },
    /// The build or publish failed; the failure is recorded per the
    /// error contract and the typed category is carried.
    Failed {
        /// The typed failure category.
        category: IndexErrorCategory,
    },
}

/// The engine-owned statistics of one explicit index (PB-4056 subset).
///
/// The typed subset of V1's `IndexStats` the engine owns; the
/// field-by-field mapping with V1 anchors is decision D7 of
/// `docs/index/gapq18-lifecycle-staleness.md`. Rates and percentages are
/// deliberately absent: they are performance statements, and only
/// Repository benchmark receipts carry those (charter PD-2026-08-26-03).
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct IndexStatistics {
    tenant: TenantId,
    name: String,
    kind: IndexKind,
    state: IndexOperationalState,
    priority: IndexPriority,
    lock_mode: IndexLockMode,
    staleness: IndexStaleness,
    covered: Option<u64>,
    published: Option<crate::query::ObservedGeneration>,
    entries: usize,
    attempts: u64,
    successes: u64,
    failures: u64,
    recorded_errors: usize,
    created_at: Instant,
    last_indexed: Option<Instant>,
    last_queried: Option<Instant>,
}

impl IndexStatistics {
    /// Creates one view. Crate-only: the catalog is the only producer.
    #[allow(clippy::too_many_arguments, reason = "one frozen view shape")]
    pub(crate) const fn new(
        tenant: TenantId,
        name: String,
        kind: IndexKind,
        state: IndexOperationalState,
        priority: IndexPriority,
        lock_mode: IndexLockMode,
        staleness: IndexStaleness,
        covered: Option<u64>,
        published: Option<crate::query::ObservedGeneration>,
        entries: usize,
        attempts: u64,
        successes: u64,
        failures: u64,
        recorded_errors: usize,
        created_at: Instant,
        last_indexed: Option<Instant>,
        last_queried: Option<Instant>,
    ) -> Self {
        Self {
            tenant,
            name,
            kind,
            state,
            priority,
            lock_mode,
            staleness,
            covered,
            published,
            entries,
            attempts,
            successes,
            failures,
            recorded_errors,
            created_at,
            last_indexed,
            last_queried,
        }
    }

    /// Returns the owning tenant.
    #[must_use]
    pub fn tenant(&self) -> &TenantId {
        &self.tenant
    }

    /// Returns the catalog name.
    #[must_use]
    pub fn name(&self) -> &str {
        &self.name
    }

    /// Returns the index kind.
    #[must_use]
    pub const fn kind(&self) -> IndexKind {
        self.kind
    }

    /// Returns the operational state.
    #[must_use]
    pub const fn state(&self) -> IndexOperationalState {
        self.state
    }

    /// Returns the deployment priority.
    #[must_use]
    pub const fn priority(&self) -> IndexPriority {
        self.priority
    }

    /// Returns the definition-change lock mode.
    #[must_use]
    pub const fn lock_mode(&self) -> IndexLockMode {
        self.lock_mode
    }

    /// Returns the staleness report with its typed reasons.
    #[must_use]
    pub const fn staleness(&self) -> &IndexStaleness {
        &self.staleness
    }

    /// Returns the covered storage sequence of the newest published
    /// segment, when one exists.
    #[must_use]
    pub const fn covered_sequence(&self) -> Option<u64> {
        self.covered
    }

    /// Returns the newest published generation, when one exists.
    #[must_use]
    pub const fn published(&self) -> Option<crate::query::ObservedGeneration> {
        self.published
    }

    /// Returns the entry count of the published segment.
    #[must_use]
    pub const fn entries(&self) -> usize {
        self.entries
    }

    /// Returns the number of build attempts (V1 `MapAttempts`).
    #[must_use]
    pub const fn build_attempts(&self) -> u64 {
        self.attempts
    }

    /// Returns the number of successful builds (V1 `MapSuccesses`).
    #[must_use]
    pub const fn build_successes(&self) -> u64 {
        self.successes
    }

    /// Returns the number of failed builds (V1 `MapErrors`).
    #[must_use]
    pub const fn build_failures(&self) -> u64 {
        self.failures
    }

    /// Returns the number of retained recorded errors.
    #[must_use]
    pub const fn recorded_errors(&self) -> usize {
        self.recorded_errors
    }

    /// Returns the registration instant (V1 `CreatedTimestamp`).
    #[must_use]
    pub const fn created_at(&self) -> Instant {
        self.created_at
    }

    /// Returns the instant of the last successful publication (V1
    /// `LastIndexingTime`), when one exists.
    #[must_use]
    pub const fn last_indexed(&self) -> Option<Instant> {
        self.last_indexed
    }

    /// Returns the instant of the last usage report (V1
    /// `LastQueryingTime`), when one arrived.
    #[must_use]
    pub const fn last_queried(&self) -> Option<Instant> {
        self.last_queried
    }
}

/// The per-tenant aggregate of the engine-owned index statistics (the
/// GAP-D18 engine-side residual).
///
/// One row per tenant that owns explicit indexes, in tenant name order.
/// Counts and totals are aggregates of the per-index views; nothing here
/// carries document data.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct TenantIndexStatistics {
    tenant: TenantId,
    normal: usize,
    paused: usize,
    disabled: usize,
    error: usize,
    attempts: u64,
    successes: u64,
    failures: u64,
    recorded_errors: usize,
}

impl TenantIndexStatistics {
    /// Creates one aggregate row. Crate-only: the catalog is the only
    /// producer.
    #[allow(clippy::too_many_arguments, reason = "one frozen view shape")]
    pub(crate) const fn new(
        tenant: TenantId,
        normal: usize,
        paused: usize,
        disabled: usize,
        error: usize,
        attempts: u64,
        successes: u64,
        failures: u64,
        recorded_errors: usize,
    ) -> Self {
        Self {
            tenant,
            normal,
            paused,
            disabled,
            error,
            attempts,
            successes,
            failures,
            recorded_errors,
        }
    }

    /// Returns the owning tenant.
    #[must_use]
    pub fn tenant(&self) -> &TenantId {
        &self.tenant
    }

    /// Returns the number of the tenant's explicit indexes in one state.
    #[must_use]
    pub const fn count_in(&self, state: IndexOperationalState) -> usize {
        match state {
            IndexOperationalState::Normal => self.normal,
            IndexOperationalState::Paused => self.paused,
            IndexOperationalState::Disabled => self.disabled,
            IndexOperationalState::Error => self.error,
        }
    }

    /// Returns the tenant's total number of explicit indexes.
    #[must_use]
    pub const fn total(&self) -> usize {
        self.normal + self.paused + self.disabled + self.error
    }

    /// Returns the tenant's aggregated build attempts.
    #[must_use]
    pub const fn build_attempts(&self) -> u64 {
        self.attempts
    }

    /// Returns the tenant's aggregated successful builds.
    #[must_use]
    pub const fn build_successes(&self) -> u64 {
        self.successes
    }

    /// Returns the tenant's aggregated failed builds.
    #[must_use]
    pub const fn build_failures(&self) -> u64 {
        self.failures
    }

    /// Returns the tenant's total number of retained recorded errors.
    #[must_use]
    pub const fn recorded_errors(&self) -> usize {
        self.recorded_errors
    }
}

/// An index store borrowed from one Engine: the typed lifecycle surface.
///
/// The store is bound to its Engine's lifetime and its engine-owned index
/// catalog, exactly like [`Engine::queries`](crate::engine::Engine::queries).
/// [`Engine::indexes`](crate::engine::Engine::indexes) is its only
/// production acquisition path.
///
/// ```compile_fail
/// // No store may outlive its Engine. This must not compile: `shutdown`
/// // consumes the Engine while the borrowed store is still live.
/// use terrabase::{Engine, EngineConfig};
/// let engine = Engine::open(EngineConfig::durable("/tmp/terrabase-docstore").unwrap()).unwrap();
/// let store = engine.indexes();
/// engine.shutdown().unwrap();
/// drop(store);
/// ```
///
/// ```compile_fail
/// // There is no independent index-store constructor. This must not
/// // compile.
/// let store = terrabase::index::operations::IndexStore::open("/tmp/other");
/// ```
pub struct IndexStore<'engine> {
    engine: &'engine Engine,
    catalog: Arc<IndexCatalog>,
}

impl<'engine> IndexStore<'engine> {
    /// Binds one store to one Engine and its engine-owned index catalog.
    /// Crate-only: the durable Engine is the only production acquisition
    /// path.
    pub(crate) fn from_engine(engine: &'engine Engine) -> Self {
        Self { engine, catalog: engine.index_catalog() }
    }

    /// Creates or updates one explicit index under a caller-chosen name.
    ///
    /// The reserved `Auto/` namespace refuses creation, and the
    /// definition must be explicit. On an existing namespace the stored
    /// lock mode governs: an unlocked index accepts a different
    /// definition in place, the locked-ignore mode accepts the call as a
    /// recorded no-op, and the locked-error mode fails closed. Identical
    /// content is never a definition change.
    ///
    /// # Errors
    ///
    /// Returns [`IndexOperationError::AutoNamespaceReserved`] for the
    /// reserved namespace, [`IndexOperationError::KindMismatch`] for a
    /// non-explicit definition, and
    /// [`IndexOperationError::DefinitionLocked`] when a changed
    /// definition meets the locked-error mode.
    pub fn create(
        &self,
        namespace: &IndexNamespace,
        definition: &IndexDefinition,
        deployment: IndexDeployment,
    ) -> Result<IndexCreationOutcome, IndexOperationError> {
        self.catalog.deploy_explicit(namespace, definition, deployment)
    }

    /// Deletes one explicit index.
    ///
    /// The namespace is removed and a bounded tombstone keeps the
    /// deletion observable; pending supersessions that name it are
    /// dropped. Automatic indexes refuse deletion here — the optimizer
    /// owns their retirement.
    ///
    /// # Errors
    ///
    /// Returns [`IndexOperationError::NamespaceUnknown`] for an
    /// unregistered namespace and
    /// [`IndexOperationError::NotExplicitIndex`] for an automatic index.
    pub fn delete(&self, namespace: &IndexNamespace) -> Result<(), IndexOperationError> {
        self.catalog.delete_explicit(namespace)
    }

    /// Resumes a paused index (V1 `StartIndex`).
    ///
    /// # Errors
    ///
    /// Returns [`IndexOperationError::IllegalTransition`] from every
    /// state but [`Paused`](IndexOperationalState::Paused); a
    /// [`Normal`](IndexOperationalState::Normal) index is a recorded
    /// no-op.
    pub fn start(
        &self,
        namespace: &IndexNamespace,
    ) -> Result<LifecycleTransition, IndexOperationError> {
        self.catalog.transition_operational(namespace, IndexLifecycleOperation::Start)
    }

    /// Pauses an index (V1 `StopIndex`): no indexing, still queryable.
    ///
    /// # Errors
    ///
    /// Returns [`IndexOperationError::IllegalTransition`] from
    /// [`Disabled`](IndexOperationalState::Disabled) and
    /// [`Error`](IndexOperationalState::Error); a
    /// [`Paused`](IndexOperationalState::Paused) index is a recorded
    /// no-op.
    pub fn stop(
        &self,
        namespace: &IndexNamespace,
    ) -> Result<LifecycleTransition, IndexOperationError> {
        self.catalog.transition_operational(namespace, IndexLifecycleOperation::Stop)
    }

    /// Clears a persistent disable (V1 `EnableIndex`).
    ///
    /// # Errors
    ///
    /// Returns [`IndexOperationError::IllegalTransition`] from
    /// [`Error`](IndexOperationalState::Error); indexes that are not
    /// disabled are a recorded no-op.
    pub fn enable(
        &self,
        namespace: &IndexNamespace,
    ) -> Result<LifecycleTransition, IndexOperationError> {
        self.catalog.transition_operational(namespace, IndexLifecycleOperation::Enable)
    }

    /// Persistently disables an index (V1 `DisableIndex`).
    ///
    /// # Errors
    ///
    /// Returns [`IndexOperationError::IllegalTransition`] from
    /// [`Error`](IndexOperationalState::Error); an already-disabled index
    /// is a recorded no-op.
    pub fn disable(
        &self,
        namespace: &IndexNamespace,
    ) -> Result<LifecycleTransition, IndexOperationError> {
        self.catalog.transition_operational(namespace, IndexLifecycleOperation::Disable)
    }

    /// Drops indexed data and re-indexes, returning the index to the
    /// normal running state from every state (V1 `ResetIndexOperation`).
    ///
    /// The in-place mode keeps the epoch and clears published segments
    /// and history; the side-by-side mode allocates the next epoch while
    /// the old one keeps serving until the replacement's first
    /// publication cuts over. Recorded errors and the failure streak are
    /// cleared.
    ///
    /// # Errors
    ///
    /// Returns [`IndexOperationError::NamespaceUnknown`] for an
    /// unregistered namespace and
    /// [`IndexOperationError::NotExplicitIndex`] for an automatic index.
    pub fn reset(
        &self,
        namespace: &IndexNamespace,
        mode: IndexResetMode,
    ) -> Result<LifecycleTransition, IndexOperationError> {
        self.catalog.reset_index(namespace, mode)
    }

    /// Sets the definition-change lock mode (V1's lock-mode operation).
    ///
    /// # Errors
    ///
    /// Returns [`IndexOperationError::NamespaceUnknown`] for an
    /// unregistered namespace and
    /// [`IndexOperationError::NotExplicitIndex`] for an automatic index.
    pub fn set_lock(
        &self,
        namespace: &IndexNamespace,
        mode: IndexLockMode,
    ) -> Result<(), IndexOperationError> {
        self.catalog.set_lock_mode(namespace, mode)
    }

    /// Clears recorded errors and leaves the error state.
    ///
    /// # Errors
    ///
    /// Returns [`IndexOperationError::IllegalTransition`] from every
    /// state but [`Error`](IndexOperationalState::Error): clearing an
    /// error list that does not exist is an ambiguous request, and the
    /// seam prefers one typed refusal.
    pub fn clear_errors(
        &self,
        namespace: &IndexNamespace,
    ) -> Result<LifecycleTransition, IndexOperationError> {
        self.catalog.clear_recorded_errors(namespace)
    }

    /// Returns the operational state of one index.
    ///
    /// # Errors
    ///
    /// Returns [`IndexOperationError::NamespaceUnknown`] for an
    /// unregistered namespace and
    /// [`IndexOperationError::NotExplicitIndex`] for an automatic index.
    pub fn state(
        &self,
        namespace: &IndexNamespace,
    ) -> Result<IndexOperationalState, IndexOperationError> {
        self.catalog.operational_state_of(namespace)
    }

    /// Returns the staleness of one index with its typed reasons.
    ///
    /// # Errors
    ///
    /// Returns [`IndexOperationError::NamespaceUnknown`] for an
    /// unregistered namespace (automatic and explicit indexes both serve
    /// staleness; [`IndexOperationError::NotExplicitIndex`] is reserved
    /// for the operations that require an explicit index).
    pub fn staleness(
        &self,
        namespace: &IndexNamespace,
    ) -> Result<IndexStaleness, IndexOperationError> {
        self.catalog.staleness_of(namespace).ok_or(IndexOperationError::NamespaceUnknown)
    }

    /// Runs one bounded rebuild step for one index.
    ///
    /// This is the engine-owned maintenance body a host schedules
    /// (cadence is host-side per charter PD-2026-08-26-01 D1): the step
    /// builds and publishes a segment from the latest observed
    /// publication when coverage trails, refuses to index in a non-normal
    /// state, and records successes and failures per the error contract.
    /// It holds no lock across an Engine call.
    ///
    /// # Errors
    ///
    /// Returns [`IndexOperationError::NamespaceUnknown`] for an
    /// unregistered namespace and
    /// [`IndexOperationError::NotExplicitIndex`] for an automatic index.
    pub fn rebuild(
        &self,
        namespace: &IndexNamespace,
    ) -> Result<RebuildStepOutcome, IndexOperationError> {
        self.step(namespace)
    }

    /// Waits, deadline-bounded, for one index to become non-stale.
    ///
    /// The wait never sleeps: each iteration evaluates staleness and, if
    /// stale and the bound has not hit, runs at most one bounded rebuild
    /// step — the engine drives progress instead of parking. It ends
    /// non-stale, when the bound hits (checked before every step, so a
    /// zero bound evaluates once and never steps), or when a step can
    /// make no progress (a paused, disabled, or errored index, or one
    /// already up to date) — whichever comes first. The outcome carries
    /// the final staleness; the timeout error is the caller's to raise,
    /// exactly as V1's client does.
    ///
    /// # Errors
    ///
    /// Returns [`IndexOperationError::NamespaceUnknown`] for an
    /// unregistered namespace and
    /// [`IndexOperationError::NotExplicitIndex`] for an automatic index.
    pub fn wait_for_non_stale(
        &self,
        namespace: &IndexNamespace,
        bound: WaitForNonStale,
    ) -> Result<NonStaleWaitOutcome, IndexOperationError> {
        let started = self.catalog.now();
        loop {
            let staleness = self.staleness(namespace)?;
            if !staleness.stale() {
                return Ok(NonStaleWaitOutcome::NonStale(staleness));
            }
            if self.catalog.now().saturating_duration_since(started) >= bound.timeout() {
                return Ok(NonStaleWaitOutcome::TimedOut(staleness));
            }
            let progressed = matches!(self.step(namespace)?, RebuildStepOutcome::Published { .. });
            if !progressed {
                return Ok(NonStaleWaitOutcome::TimedOut(staleness));
            }
        }
    }

    /// Returns one tenant's recorded index errors, in catalog order.
    #[must_use]
    pub fn errors(&self, tenant: &TenantId) -> Vec<IndexErrorListing> {
        self.catalog.errors_of(tenant)
    }

    /// Returns one tenant's explicit-index statistics, in catalog order.
    #[must_use]
    pub fn statistics(&self, tenant: &TenantId) -> Vec<IndexStatistics> {
        self.catalog.statistics_of(tenant)
    }

    /// Returns the engine-wide per-tenant index statistics aggregate, in
    /// tenant name order.
    #[must_use]
    pub fn engine_statistics(&self) -> Vec<TenantIndexStatistics> {
        self.catalog.engine_statistics()
    }

    /// The body of one bounded rebuild step, shared by
    /// [`Self::rebuild`] and [`Self::wait_for_non_stale`].
    fn step(&self, namespace: &IndexNamespace) -> Result<RebuildStepOutcome, IndexOperationError> {
        let state = self.catalog.operational_state_of(namespace)?;
        if state != IndexOperationalState::Normal {
            return Ok(RebuildStepOutcome::Skipped { state });
        }
        let Some(delta) = self.catalog.delta() else {
            return Ok(RebuildStepOutcome::UpToDate);
        };
        if self
            .catalog
            .covered_sequence_of(namespace)
            .is_some_and(|covered| covered >= delta.covered_sequence())
        {
            return Ok(RebuildStepOutcome::UpToDate);
        }
        self.catalog.note_operation_attempt(namespace);
        let builder = IndexBuilder::new(&self.catalog, self.engine);
        match builder.rebuild_from(namespace, &delta, self.engine.identity()) {
            Ok(RebuildOutcome::Published { epoch, generation, covered_sequence }) => {
                self.catalog.note_operation_success(namespace);
                Ok(RebuildStepOutcome::Published { epoch, generation, covered_sequence })
            }
            // A replayed or reordered build cannot lower the visible
            // generation; the step treats it as already up to date.
            Ok(RebuildOutcome::RejectedAsStale { .. }) => Ok(RebuildStepOutcome::UpToDate),
            Err(failure) => {
                let category = IndexErrorCategory::of(failure);
                self.catalog.note_operation_failure(namespace, IndexErrorAction::Build, category);
                Ok(RebuildStepOutcome::Failed { category })
            }
        }
    }
}
