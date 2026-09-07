//! Engine-owned online shard split worker.
//!
//! This is the production call graph for a split: the durable lifecycle
//! runtime admits the operation, the snapshot copier fills the target, the
//! retained publication journal catches up its tail, and the runtime performs
//! the fenced route CAS.  Callers advance it in bounded steps so a worker
//! cannot monopolize the engine.

use std::sync::Arc;

use crate::shard_split::{
    RangeCopier, ShardRouter, ShardSplit, SplitOrchestratorAdapter, SplitPhase, SplitRuntime,
    SplitRuntimeError, WriterFence,
};

use super::{
    Engine, EngineError,
    shard_catchup::ShardJournalCatchUp,
    shard_split::{
        RouteCatalogError, ScopedShardEngine, ScopedShardRangeSource, ScopedShardRangeTarget,
    },
};
use crate::types::TenantScope;

/// Work completed by one supervisor step.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub(crate) enum SplitStep {
    /// A bounded snapshot or journal page was applied.
    Progress,
    /// The route was cut over, verified, and the split completed.
    Complete { generation: u64 },
}

/// Failure while driving the real engine split path.
#[derive(Debug, Eq, PartialEq)]
pub(crate) enum SplitSupervisorError<OE> {
    /// The generic runtime rejected lifecycle, placement, or route work.
    Runtime(SplitRuntimeError<RouteCatalogError, EngineError, RouteCatalogError, OE>),
    /// The engine journal could not provide a complete bounded tail page.
    CatchUp(super::shard_catchup::ShardCatchUpError),
    /// The target could not be opened.
    TargetOpen(super::EngineOpenError),
    /// No durable route exists from which to admit a newly started worker.
    MissingRoute,
}

/// Drives one admitted database split through the production adapters.
pub(crate) struct ShardSplitSupervisor<O>
where
    O: SplitOrchestratorAdapter,
{
    runtime: SplitRuntime<ScopedShardEngine, O>,
    engine: Arc<Engine>,
    source: ScopedShardRangeSource,
    target: Arc<Engine>,
    copier: Option<RangeCopier>,
    catchup: Option<ShardJournalCatchUp>,
    scope: TenantScope,
    journal_since_checkpoint: usize,
}

impl<O> ShardSplitSupervisor<O>
where
    O: SplitOrchestratorAdapter,
{
    /// Admits a new split and opens the real source and target adapters.
    pub(crate) fn start(
        engine: Arc<Engine>,
        scope: TenantScope,
        split: ShardSplit,
        orchestrator: O,
        router: ShardRouter,
    ) -> Result<Self, SplitSupervisorError<O::Error>> {
        let adapter = ScopedShardEngine::new(Arc::clone(&engine), scope.clone())
            .map_err(|_| SplitSupervisorError::MissingRoute)?;
        let context = split.context().ok_or(SplitSupervisorError::MissingRoute)?;
        let mut runtime = SplitRuntime::new(adapter, orchestrator, router);
        runtime.admit(split).map_err(SplitSupervisorError::Runtime)?;
        let target = Arc::new(
            ScopedShardEngine::new(Arc::clone(&engine), scope.clone())
                .map_err(|_| SplitSupervisorError::MissingRoute)?
                .open_existing_target(&context)
                .map_err(SplitSupervisorError::TargetOpen)?,
        );
        let source = ScopedShardRangeSource::new(Arc::clone(&engine), &scope)
            .map_err(|_| SplitSupervisorError::MissingRoute)?;
        let state = runtime.split().expect("admitted split").durable_state();
        let copier =
            RangeCopier::new(state.lower().map(ToOwned::to_owned), state.split_key().to_vec())
                .map_err(|_| SplitSupervisorError::MissingRoute)?;
        Ok(Self {
            runtime,
            engine,
            source,
            target,
            copier: Some(copier),
            catchup: None,
            scope,
            journal_since_checkpoint: 0,
        })
    }

    /// Reopens a durable split after a process restart.
    pub(crate) fn resume(
        engine: Arc<Engine>,
        scope: TenantScope,
        orchestrator: O,
        router: ShardRouter,
    ) -> Result<Option<Self>, SplitSupervisorError<O::Error>> {
        let adapter = ScopedShardEngine::new(Arc::clone(&engine), scope.clone())
            .map_err(|_| SplitSupervisorError::MissingRoute)?;
        let mut runtime = SplitRuntime::new(adapter, orchestrator, router);
        if !runtime.resume().map_err(SplitSupervisorError::Runtime)? {
            return Ok(None);
        }
        let split = runtime.split().expect("resumed split");
        let context = split.context().ok_or(SplitSupervisorError::MissingRoute)?;
        let target = Arc::new(
            ScopedShardEngine::new(Arc::clone(&engine), scope.clone())
                .map_err(|_| SplitSupervisorError::MissingRoute)?
                .open_existing_target(&context)
                .map_err(SplitSupervisorError::TargetOpen)?,
        );
        let source = ScopedShardRangeSource::new(Arc::clone(&engine), &scope)
            .map_err(|_| SplitSupervisorError::MissingRoute)?;
        let (copier, catchup) = match split.phase() {
            SplitPhase::Planned | SplitPhase::Backfilling => (
                Some(
                    RangeCopier::new(
                        split.lower().map(ToOwned::to_owned),
                        split.split_key().to_vec(),
                    )
                    .map_err(|_| SplitSupervisorError::MissingRoute)?,
                ),
                None,
            ),
            SplitPhase::CatchingUp => (
                None,
                Some(
                    ShardJournalCatchUp::new(
                        Arc::clone(&engine),
                        Arc::clone(&target),
                        scope.clone(),
                        target.scope_for(scope.tenant()),
                        &split.durable_state(),
                        split.durable_state().target_applied_through(),
                    )
                    .map_err(SplitSupervisorError::CatchUp)?,
                ),
            ),
            SplitPhase::CutOver | SplitPhase::Complete | SplitPhase::Aborted => (None, None),
        };
        Ok(Some(Self {
            runtime,
            engine,
            source,
            target,
            copier,
            catchup,
            scope,
            journal_since_checkpoint: 0,
        }))
    }

    /// Applies one bounded page and advances the split state machine.
    pub(crate) fn step(
        &mut self,
        copy_limit: usize,
        catchup_limit: u32,
    ) -> Result<SplitStep, SplitSupervisorError<O::Error>> {
        // A committed route survives publication, verification, and cleanup
        // failures. Finish that operation before touching the old copy/tail
        // workers, including when retrying without a process restart.
        if self.runtime.split().is_some_and(|split| {
            matches!(split.phase(), SplitPhase::CutOver | SplitPhase::Complete)
        }) {
            self.runtime.complete().map_err(SplitSupervisorError::Runtime)?;
            self.copier = None;
            self.catchup = None;
            return Ok(SplitStep::Complete {
                generation: self.runtime.router().table().generation(),
            });
        }
        if let Some(copier) = &mut self.copier {
            let mut target = ScopedShardRangeTarget::open(Arc::clone(&self.target), &self.scope);
            if !copier
                .step(&mut self.source, &mut target, copy_limit)
                .map_err(|_| SplitSupervisorError::MissingRoute)?
            {
                return Ok(SplitStep::Progress);
            }
            let state = self.runtime.split().expect("active split").durable_state();
            let through = self.source_boundary()?;
            self.runtime.begin_catch_up(through).map_err(SplitSupervisorError::Runtime)?;
            self.catchup = Some(
                ShardJournalCatchUp::new(
                    Arc::clone(&self.engine),
                    Arc::clone(&self.target),
                    self.scope.clone(),
                    self.target.scope_for(self.scope.tenant()),
                    &state,
                    0,
                )
                .map_err(SplitSupervisorError::CatchUp)?,
            );
            self.copier = None;
            return Ok(SplitStep::Progress);
        }
        if let Some(catchup) = &mut self.catchup {
            self.journal_since_checkpoint +=
                catchup.step(catchup_limit).map_err(SplitSupervisorError::CatchUp)?;
            let high = catchup.source_high_water().map_err(SplitSupervisorError::CatchUp)?;
            if catchup.applied_through() < high {
                // A checkpoint itself adds one source publication. Amortize
                // that write so even one-group pages eventually reach the tail.
                if self.journal_since_checkpoint >= 1024 {
                    self.runtime
                        .note_frontiers(high, catchup.applied_through(), high)
                        .map_err(SplitSupervisorError::Runtime)?;
                    self.journal_since_checkpoint = 0;
                }
                return Ok(SplitStep::Progress);
            }
            let context = self
                .runtime
                .split()
                .and_then(ShardSplit::context)
                .ok_or(SplitSupervisorError::MissingRoute)?;
            ScopedShardEngine::new(Arc::clone(&self.engine), self.scope.clone())
                .map_err(|_| SplitSupervisorError::MissingRoute)?
                .prepare_split_verification(&context, high)
                .map_err(|error| SplitSupervisorError::Runtime(SplitRuntimeError::Data(error)))?;
            // Each attempt obtains a fresh permit. The engine's exclusive
            // commit barrier performs the actual fence and may reject a stale
            // frontier when a writer wins the race.
            let mut fence = WriterFence::new();
            fence.begin_drain(high).map_err(|_| SplitSupervisorError::MissingRoute)?;
            fence.note_acked(high).map_err(|_| SplitSupervisorError::MissingRoute)?;
            let permit = fence
                .try_fence(catchup.applied_through())
                .map_err(|_| SplitSupervisorError::MissingRoute)?;
            let generation = self
                .runtime
                .cut_over_caught_up(permit, high)
                .map_err(SplitSupervisorError::Runtime)?;
            self.runtime.complete().map_err(SplitSupervisorError::Runtime)?;
            self.catchup = None;
            return Ok(SplitStep::Complete { generation });
        }
        Err(SplitSupervisorError::MissingRoute)
    }

    fn source_boundary(&self) -> Result<u64, SplitSupervisorError<O::Error>> {
        self.engine
            .published_sequence()
            .map_err(|_| SplitSupervisorError::MissingRoute)
            .map(|sequence| sequence.get())
    }
}
