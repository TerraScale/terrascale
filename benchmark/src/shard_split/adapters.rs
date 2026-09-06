//! Explicit seams for wiring split state to the engine and orchestrator.
//!
//! The split state machine is deliberately storage-neutral.  These traits
//! make the integration boundary auditable: an implementation must provide
//! one transaction for the writer fence, route replacement, and durable split
//! record.  Merely calling [`ShardRouter::cut_over`] is not sufficient for a
//! production member because it cannot atomically update the engine catalog.

use super::{CutoverPermit, DurableSplitState, RouteTable, SplitContext, SplitStateStore};

/// The facts an orchestrator must validate before a cutover.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct CutoverContext {
    /// Operation identity and database incarnation.
    pub split: SplitContext,
    /// Route generation read before the compare-and-swap.
    pub route_generation: u64,
    /// Highest source sequence drained by the writer fence.
    pub drained_through: u64,
    /// Opaque fence permit proving new writes are stopped.
    pub permit: CutoverPermit,
}

/// Route admission attached by Member to one origin transaction.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct ShardAdmissionToken {
    /// Database incarnation that issued the token.
    pub database_incarnation: u128,
    /// Route generation observed by the serving router.
    pub route_generation: u64,
    /// Shard selected by that router.
    pub shard_id: u64,
    /// Keys routed by the serving request, all to `shard_id`.
    pub keys: Vec<Vec<u8>>,
}

impl ShardAdmissionToken {
    /// Creates a token for one router-selected key.
    #[must_use]
    pub fn for_key(
        database_incarnation: u128,
        route_generation: u64,
        shard_id: u64,
        key: &[u8],
    ) -> Self {
        Self::for_keys(database_incarnation, route_generation, shard_id, [key])
    }

    /// Creates a token for a bounded set of keys on one shard.
    #[must_use]
    pub fn for_keys<'a>(
        database_incarnation: u128,
        route_generation: u64,
        shard_id: u64,
        keys: impl IntoIterator<Item = &'a [u8]>,
    ) -> Self {
        Self {
            database_incarnation,
            route_generation,
            shard_id,
            keys: keys.into_iter().map(<[u8]>::to_vec).collect(),
        }
    }
}

/// Durable route receipt returned while recovering an already committed split.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct CommittedSplitRoute {
    /// Operation identity whose route was installed.
    pub split: SplitContext,
    /// Durable route generation assigned by the engine.
    pub generation: u64,
    /// The authoritative route table to install in the serving mirror.
    pub route: RouteTable,
}

/// Engine-owned atomic cutover seam.
pub trait AtomicSplitCutover {
    /// Engine-specific error.
    type Error;

    /// Fences writes, replaces routing, and persists the cutover phase in one
    /// durable transaction. Implementations must return the installed route
    /// generation and make an exact retry a no-op.
    fn install_split_route(
        &mut self,
        context: &CutoverContext,
        state: &DurableSplitState,
    ) -> Result<u64, Self::Error>;

    /// Recovers the durable route receipt for an operation after restart.
    fn recover_split_route(
        &mut self,
        split: &SplitContext,
    ) -> Result<Option<CommittedSplitRoute>, Self::Error>;
}

/// Engine hooks required to drive a resumable split.
pub trait SplitEngineAdapter: SplitStateStore + AtomicSplitCutover {
    /// Engine-specific error for data movement and verification.
    type DataError;

    /// Creates the target SQLite file, idempotently, before backfill starts.
    fn create_split_target(&mut self, context: &SplitContext) -> Result<(), Self::DataError>;

    /// Removes an unreferenced target after a pre-cutover abort. This must be
    /// idempotent and must refuse to remove a target after routing is moved.
    fn rollback_split_target(&mut self, context: &SplitContext) -> Result<(), Self::DataError>;

    /// Verifies source and target facts after routing has moved.
    fn verify_split_target(&mut self, context: &SplitContext) -> Result<(), Self::DataError>;

    /// Removes source rows only after successful target verification.
    fn cleanup_split_source(&mut self, context: &SplitContext) -> Result<(), Self::DataError>;
}

/// Orchestrator hooks for placement and session/frontier propagation.
pub trait SplitOrchestratorAdapter {
    /// Orchestrator-specific error.
    type Error;

    /// Confirms that the database incarnation is still hosted in the same
    /// placement epoch. A mismatch must fail closed and trigger recovery.
    fn validate_split_placement(
        &mut self,
        database_incarnation: u128,
        placement_epoch: u64,
    ) -> Result<(), Self::Error>;

    /// Publishes the new route generation and session frontier after the
    /// engine transaction commits. Publication is idempotent by split id.
    fn publish_split_frontier(
        &mut self,
        context: &SplitContext,
        route_generation: u64,
    ) -> Result<(), Self::Error>;
}

/// Integration checklist surfaced by adapters and startup diagnostics.
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct SplitWiringReport {
    /// The engine persists and reloads split records.
    pub state_store: bool,
    /// The engine has an atomic fence/route/state transaction.
    pub atomic_cutover: bool,
    /// The engine implements idempotent target creation and rollback.
    pub target_lifecycle: bool,
    /// The orchestrator validates placement epochs.
    pub placement_validation: bool,
    /// Session/frontier propagation is connected to the router and SDK.
    pub frontier_propagation: bool,
}

impl SplitWiringReport {
    /// Returns whether every required production integration is present.
    #[must_use]
    pub const fn is_ready(self) -> bool {
        self.state_store
            && self.atomic_cutover
            && self.target_lifecycle
            && self.placement_validation
            && self.frontier_propagation
    }
}
