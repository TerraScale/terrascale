//! Serving-runtime binding for online shard splits.
//!
//! The engine and orchestrator implementations remain outside this module.
//! This binding owns the ordering between durable split state, placement
//! validation, route installation, and session-frontier publication.

use super::{
    AtomicSplitCutover, CutoverContext, CutoverPermit, ShardRouter, ShardSplit, SplitEngineAdapter,
    SplitOrchestratorAdapter, SplitPhase, SplitStateStore,
};

/// Failure while driving a serving split.
#[derive(Debug, Eq, PartialEq)]
pub enum SplitRuntimeError<SE, DE, CE, OE> {
    /// No operation context was attached to the split.
    MissingContext,
    /// Durable split state could not be loaded or saved.
    State(SE),
    /// Target creation, verification, or cleanup failed.
    Data(DE),
    /// Placement validation failed.
    Placement(OE),
    /// The engine rejected an atomic cutover.
    Cutover(CE),
    /// The serving router rejected the committed route mirror.
    Routing(super::RouteError),
    /// Frontier publication failed after the route was committed.
    Publication(OE),
    /// The split is not at the phase required by this operation.
    WrongPhase(SplitPhase),
    /// The route was committed but no generation was returned for publication.
    MissingCommittedGeneration,
}

/// Runtime owner for one admitted database's split route and split record.
pub struct SplitRuntime<E, O>
where
    E: SplitEngineAdapter,
    O: SplitOrchestratorAdapter,
{
    engine: E,
    orchestrator: O,
    router: ShardRouter,
    split: Option<ShardSplit>,
    committed_generation: Option<u64>,
}

impl<E, O> SplitRuntime<E, O>
where
    E: SplitEngineAdapter,
    O: SplitOrchestratorAdapter,
{
    /// Binds an engine and orchestrator to the durable route table read at
    /// member startup. The supplied table is immutable until cutover.
    #[must_use]
    pub fn new(engine: E, orchestrator: O, router: ShardRouter) -> Self {
        Self { engine, orchestrator, router, split: None, committed_generation: None }
    }

    /// Returns the current serving router.
    #[must_use]
    pub const fn router(&self) -> &ShardRouter {
        &self.router
    }

    /// Returns the durable split currently being driven, if any.
    #[must_use]
    pub const fn split(&self) -> Option<&ShardSplit> {
        self.split.as_ref()
    }

    /// Admits a split: placement is checked and target ownership is persisted
    /// before any copy worker can run.
    pub fn admit(
        &mut self,
        mut split: ShardSplit,
    ) -> Result<
        (),
        SplitRuntimeError<
            <E as SplitStateStore>::Error,
            E::DataError,
            <E as AtomicSplitCutover>::Error,
            O::Error,
        >,
    > {
        let context = split.context().ok_or(SplitRuntimeError::MissingContext)?;
        self.orchestrator
            .validate_split_placement(context.database_incarnation(), context.placement_epoch())
            .map_err(SplitRuntimeError::Placement)?;
        self.engine.create_split_target(&context).map_err(SplitRuntimeError::Data)?;
        split.begin_backfill().map_err(|error| {
            SplitRuntimeError::WrongPhase(match error {
                super::SplitCutoverError::WrongPhase(phase) => phase,
                _ => SplitPhase::Planned,
            })
        })?;
        self.engine.save(&split.durable_state()).map_err(SplitRuntimeError::State)?;
        self.split = Some(split);
        self.committed_generation = None;
        Ok(())
    }

    /// Reopens the same operation after restart and validates its placement
    /// identity before allowing the serving worker to continue.
    pub fn resume(
        &mut self,
    ) -> Result<
        bool,
        SplitRuntimeError<
            <E as SplitStateStore>::Error,
            E::DataError,
            <E as AtomicSplitCutover>::Error,
            O::Error,
        >,
    > {
        let Some(state) = self.engine.load().map_err(SplitRuntimeError::State)? else {
            self.split = None;
            return Ok(false);
        };
        let split = ShardSplit::from_durable_state(state)
            .map_err(|_| SplitRuntimeError::WrongPhase(SplitPhase::Aborted))?;
        let context = split.context().ok_or(SplitRuntimeError::MissingContext)?;
        self.orchestrator
            .validate_split_placement(context.database_incarnation(), context.placement_epoch())
            .map_err(SplitRuntimeError::Placement)?;
        self.split = Some(split);
        self.committed_generation = None;
        Ok(true)
    }

    /// Persists the backfill frontier before entering journal catch-up.
    pub fn begin_catch_up(
        &mut self,
        through: u64,
    ) -> Result<
        (),
        SplitRuntimeError<
            <E as SplitStateStore>::Error,
            E::DataError,
            <E as AtomicSplitCutover>::Error,
            O::Error,
        >,
    > {
        let split =
            self.split.as_mut().ok_or(SplitRuntimeError::WrongPhase(SplitPhase::Planned))?;
        split.begin_catch_up(through).map_err(|error| {
            SplitRuntimeError::WrongPhase(match error {
                super::SplitCutoverError::WrongPhase(phase) => phase,
                _ => SplitPhase::Backfilling,
            })
        })?;
        self.engine.save(&split.durable_state()).map_err(SplitRuntimeError::State)
    }

    /// Records monotonic source, target, and acknowledged frontiers durably.
    pub fn note_frontiers(
        &mut self,
        source_high_water: u64,
        target_applied: u64,
        acknowledged: u64,
    ) -> Result<
        (),
        SplitRuntimeError<
            <E as SplitStateStore>::Error,
            E::DataError,
            <E as AtomicSplitCutover>::Error,
            O::Error,
        >,
    > {
        let split =
            self.split.as_mut().ok_or(SplitRuntimeError::WrongPhase(SplitPhase::Planned))?;
        split.note_source_high_water(source_high_water);
        split.note_target_applied(target_applied);
        split.note_acked(acknowledged);
        self.engine.save(&split.durable_state()).map_err(SplitRuntimeError::State)
    }

    /// Atomically installs routing, then publishes the session frontier.
    /// Publication is never attempted until the engine confirms its commit.
    pub fn cut_over(
        &mut self,
        permit: CutoverPermit,
        drained_through: u64,
    ) -> Result<
        u64,
        SplitRuntimeError<
            <E as SplitStateStore>::Error,
            E::DataError,
            <E as AtomicSplitCutover>::Error,
            O::Error,
        >,
    > {
        let split =
            self.split.as_mut().ok_or(SplitRuntimeError::WrongPhase(SplitPhase::Planned))?;
        if !split.can_cut_over() {
            return Err(SplitRuntimeError::WrongPhase(split.phase()));
        }
        let context = split.context().ok_or(SplitRuntimeError::MissingContext)?;
        self.orchestrator
            .validate_split_placement(context.database_incarnation(), context.placement_epoch())
            .map_err(SplitRuntimeError::Placement)?;
        let generation = self
            .engine
            .install_split_route(&CutoverContext {
                split: context,
                route_generation: self.router.table().generation(),
                drained_through,
                permit,
            })
            .map_err(SplitRuntimeError::Cutover)?;
        self.router
            .cut_over(
                context.source_shard(),
                context.target_shard(),
                split.split_key().to_vec(),
                permit,
            )
            .map_err(SplitRuntimeError::Routing)?;
        self.committed_generation = Some(generation);
        split.cut_over().map_err(|_| SplitRuntimeError::MissingCommittedGeneration)?;
        self.engine.save(&split.durable_state()).map_err(SplitRuntimeError::State)?;
        self.orchestrator
            .publish_split_frontier(&context, generation)
            .map_err(SplitRuntimeError::Publication)?;
        Ok(generation)
    }

    /// Verifies and completes a committed split, then removes source rows.
    pub fn complete(
        &mut self,
    ) -> Result<
        (),
        SplitRuntimeError<
            <E as SplitStateStore>::Error,
            E::DataError,
            <E as AtomicSplitCutover>::Error,
            O::Error,
        >,
    > {
        if self.committed_generation.is_none() {
            return Err(SplitRuntimeError::MissingCommittedGeneration);
        }
        let split =
            self.split.as_mut().ok_or(SplitRuntimeError::WrongPhase(SplitPhase::Planned))?;
        let context = split.context().ok_or(SplitRuntimeError::MissingContext)?;
        self.engine.verify_split_target(&context).map_err(SplitRuntimeError::Data)?;
        split.note_verified().map_err(|_| SplitRuntimeError::WrongPhase(split.phase()))?;
        self.engine.save(&split.durable_state()).map_err(SplitRuntimeError::State)?;
        self.engine.cleanup_split_source(&context).map_err(SplitRuntimeError::Data)
    }
}
