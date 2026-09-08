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
    /// The planned copy range or shard identities do not match the serving route.
    InvalidPlan,
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
    frontier_published: bool,
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
        Self {
            engine,
            orchestrator,
            router,
            split: None,
            committed_generation: None,
            frontier_published: false,
        }
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
        let source = self
            .router
            .table()
            .routes()
            .iter()
            .find(|route| route.shard_id() == context.source_shard())
            .ok_or(SplitRuntimeError::InvalidPlan)?;
        // The copy and the atomic route replacement must move the same range.
        // A narrower caller-supplied lower bound would otherwise omit records
        // while cutover still transfers the source's complete lower half.
        if context.split_id() == 0
            || context.database_incarnation() == 0
            || context.source_shard() == 0
            || context.target_shard() == 0
            || source.lower() != split.lower()
            || self.router.route(split.split_key()) != Some(context.source_shard())
            || self
                .router
                .table()
                .routes()
                .iter()
                .any(|route| route.shard_id() == context.target_shard())
        {
            return Err(SplitRuntimeError::InvalidPlan);
        }
        self.orchestrator
            .validate_split_placement(context.database_incarnation(), context.placement_epoch())
            .map_err(SplitRuntimeError::Placement)?;
        split.begin_backfill().map_err(|error| {
            SplitRuntimeError::WrongPhase(match error {
                super::SplitCutoverError::WrongPhase(phase) => phase,
                _ => SplitPhase::Planned,
            })
        })?;
        if let Err(error) = self.engine.save(&split.durable_state()) {
            return Err(SplitRuntimeError::State(error));
        }
        self.engine.create_split_target(&context).map_err(SplitRuntimeError::Data)?;
        self.split = Some(split);
        self.committed_generation = None;
        self.frontier_published = false;
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
        self.frontier_published = false;
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
        // Admission persists Backfilling before invoking the data adapter.
        // Replay idempotent target creation so a restart can safely resume
        // copying after a crash or a failed create response.
        if matches!(
            split.phase(),
            SplitPhase::Planned | SplitPhase::Backfilling | SplitPhase::CatchingUp
        ) {
            self.engine.create_split_target(&context).map_err(SplitRuntimeError::Data)?;
        }
        if matches!(split.phase(), SplitPhase::CutOver | SplitPhase::Complete) {
            let route = self
                .engine
                .recover_split_route(&context)
                .map_err(SplitRuntimeError::Cutover)?
                .ok_or(SplitRuntimeError::MissingCommittedGeneration)?;
            if route.split != context {
                return Err(SplitRuntimeError::MissingCommittedGeneration);
            }
            self.router = ShardRouter::from_table(route.route);
            self.committed_generation = Some(route.generation);
        }
        self.split = Some(split);
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
        let mut split =
            self.split.clone().ok_or(SplitRuntimeError::WrongPhase(SplitPhase::Planned))?;
        split.begin_catch_up(through).map_err(|error| {
            SplitRuntimeError::WrongPhase(match error {
                super::SplitCutoverError::WrongPhase(phase) => phase,
                _ => SplitPhase::Backfilling,
            })
        })?;
        // Keep the live worker at its last persisted phase when a checkpoint
        // fails. Its next bounded step must be able to retry this transition.
        self.engine.save(&split.durable_state()).map_err(SplitRuntimeError::State)?;
        self.split = Some(split);
        Ok(())
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

    /// Commits a fully applied journal frontier with the route itself. Saving
    /// an intermediate checkpoint would append another source publication and
    /// move the very frontier the worker just drained.
    pub(crate) fn cut_over_caught_up(
        &mut self,
        permit: CutoverPermit,
        through: u64,
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
        split.note_source_high_water(through);
        split.note_target_applied(through);
        split.note_acked(through);
        self.cut_over(permit, through)
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
        // The engine route is authoritative after a lost response. Reconcile
        // the complete table before attempting another cutover transaction;
        // retrying against this worker's stale mirror could fence a new route.
        if let Some(recovered) =
            self.engine.recover_split_route(&context).map_err(SplitRuntimeError::Cutover)?
        {
            if recovered.split != context {
                return Err(SplitRuntimeError::MissingCommittedGeneration);
            }
            self.router = ShardRouter::from_table(recovered.route);
            self.committed_generation = Some(recovered.generation);
            split.cut_over().map_err(|_| SplitRuntimeError::MissingCommittedGeneration)?;
            self.engine.save(&split.durable_state()).map_err(SplitRuntimeError::State)?;
            self.orchestrator
                .publish_split_frontier(&context, recovered.generation)
                .map_err(SplitRuntimeError::Publication)?;
            self.frontier_published = true;
            return Ok(recovered.generation);
        }
        let generation = self
            .engine
            .install_split_route(
                &CutoverContext {
                    split: context,
                    route_generation: self.router.table().generation(),
                    drained_through,
                    permit,
                },
                &split.durable_state(),
            )
            .map_err(SplitRuntimeError::Cutover)?;
        let routing_result = self.router.cut_over(
            context.source_shard(),
            context.target_shard(),
            split.split_key().to_vec(),
            permit,
        );
        if let Err(original) = routing_result {
            let recovered = self
                .engine
                .recover_split_route(&context)
                .map_err(SplitRuntimeError::Cutover)?
                .ok_or(SplitRuntimeError::Routing(original))?;
            if recovered.split != context || recovered.generation != generation {
                return Err(SplitRuntimeError::Routing(original));
            }
            self.router = ShardRouter::from_table(recovered.route);
        }
        self.committed_generation = Some(generation);
        split.cut_over().map_err(|_| SplitRuntimeError::MissingCommittedGeneration)?;
        self.engine.save(&split.durable_state()).map_err(SplitRuntimeError::State)?;
        self.orchestrator
            .publish_split_frontier(&context, generation)
            .map_err(SplitRuntimeError::Publication)?;
        self.frontier_published = true;
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
        let generation =
            self.committed_generation.ok_or(SplitRuntimeError::MissingCommittedGeneration)?;
        let split =
            self.split.as_mut().ok_or(SplitRuntimeError::WrongPhase(SplitPhase::Planned))?;
        let context = split.context().ok_or(SplitRuntimeError::MissingContext)?;
        if !matches!(split.phase(), SplitPhase::CutOver | SplitPhase::Complete) {
            return Err(SplitRuntimeError::WrongPhase(split.phase()));
        }
        // Publication may have failed after the durable route commit, or the
        // process may have restarted before its response. Replay it before
        // verification and cleanup; the orchestrator must accept that retry.
        if !self.frontier_published {
            self.orchestrator
                .publish_split_frontier(&context, generation)
                .map_err(SplitRuntimeError::Publication)?;
            self.frontier_published = true;
        }
        self.engine.verify_split_target(&context).map_err(SplitRuntimeError::Data)?;
        split.note_verified().map_err(|_| SplitRuntimeError::WrongPhase(split.phase()))?;
        self.engine.save(&split.durable_state()).map_err(SplitRuntimeError::State)?;
        self.engine.cleanup_split_source(&context).map_err(SplitRuntimeError::Data)
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::shard_split::{CommittedSplitRoute, DurableSplitState, SplitContext};

    #[derive(Default)]
    struct CheckpointEngine {
        state: Option<DurableSplitState>,
        fail_save: bool,
    }

    impl SplitStateStore for CheckpointEngine {
        type Error = ();

        fn load(&self) -> Result<Option<DurableSplitState>, ()> {
            Ok(self.state.clone())
        }

        fn save(&mut self, state: &DurableSplitState) -> Result<(), ()> {
            if std::mem::take(&mut self.fail_save) {
                return Err(());
            }
            self.state = Some(state.clone());
            Ok(())
        }

        fn remove(&mut self) -> Result<(), ()> {
            self.state = None;
            Ok(())
        }
    }

    impl AtomicSplitCutover for CheckpointEngine {
        type Error = ();

        fn install_split_route(
            &mut self,
            _: &CutoverContext,
            _: &DurableSplitState,
        ) -> Result<u64, ()> {
            unreachable!("checkpoint retry must not install a route")
        }

        fn recover_split_route(
            &mut self,
            _: &SplitContext,
        ) -> Result<Option<CommittedSplitRoute>, ()> {
            Ok(None)
        }
    }

    impl SplitEngineAdapter for CheckpointEngine {
        type DataError = ();

        fn create_split_target(&mut self, _: &SplitContext) -> Result<(), ()> {
            Ok(())
        }

        fn rollback_split_target(&mut self, _: &SplitContext) -> Result<(), ()> {
            unreachable!()
        }

        fn verify_split_target(&mut self, _: &SplitContext) -> Result<(), ()> {
            unreachable!()
        }

        fn cleanup_split_source(&mut self, _: &SplitContext) -> Result<(), ()> {
            unreachable!()
        }
    }

    struct Placement;

    impl SplitOrchestratorAdapter for Placement {
        type Error = ();

        fn validate_split_placement(&mut self, _: u128, _: u64) -> Result<(), ()> {
            Ok(())
        }

        fn publish_split_frontier(&mut self, _: &SplitContext, _: u64) -> Result<(), ()> {
            unreachable!("checkpoint retry must not publish a route")
        }
    }

    #[test]
    fn failed_backfill_checkpoint_can_retry_without_restart() {
        let mut runtime =
            SplitRuntime::new(CheckpointEngine::default(), Placement, ShardRouter::single(1));
        let context = SplitContext::new(1, 2, 1, 2, 3, 0);
        runtime
            .admit(ShardSplit::plan_with_context(None, b"m".to_vec(), context).unwrap())
            .unwrap();
        runtime.engine.fail_save = true;

        assert_eq!(runtime.begin_catch_up(7), Err(SplitRuntimeError::State(())));
        assert_eq!(runtime.split().unwrap().phase(), SplitPhase::Backfilling);
        assert_eq!(runtime.engine.state.as_ref().unwrap().phase(), SplitPhase::Backfilling);

        runtime.begin_catch_up(7).unwrap();
        assert_eq!(runtime.split().unwrap().phase(), SplitPhase::CatchingUp);
        assert_eq!(runtime.engine.state.as_ref().unwrap().phase(), SplitPhase::CatchingUp);
    }

    #[test]
    fn admission_rejects_copy_range_that_differs_from_routed_range() {
        let context = SplitContext::new(1, 2, 1, 2, 3, 0);
        let routes = super::super::RouteTable::new(vec![
            super::super::ShardRoute::new(1, Some(b"a".to_vec()), Some(b"z".to_vec())).unwrap(),
        ])
        .unwrap();
        for (lower, key) in [
            (None, b"m".to_vec()),
            (Some(b"b".to_vec()), b"m".to_vec()),
            (Some(b"a".to_vec()), b"z".to_vec()),
        ] {
            let mut runtime = SplitRuntime::new(
                CheckpointEngine::default(),
                Placement,
                ShardRouter::from_table(routes.clone()),
            );
            let split = ShardSplit::plan_with_context(lower, key, context).unwrap();
            assert_eq!(runtime.admit(split), Err(SplitRuntimeError::InvalidPlan));
            assert!(runtime.engine.state.is_none());
            assert!(runtime.split().is_none());
        }
        let mut runtime = SplitRuntime::new(
            CheckpointEngine::default(),
            Placement,
            ShardRouter::from_table(routes),
        );
        runtime
            .admit(
                ShardSplit::plan_with_context(Some(b"a".to_vec()), b"m".to_vec(), context).unwrap(),
            )
            .unwrap();
    }
}
