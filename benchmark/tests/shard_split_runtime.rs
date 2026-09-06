use std::{cell::RefCell, rc::Rc};
use terrabase::shard_split::{
    AtomicSplitCutover, CommittedSplitRoute, CutoverContext, DurableSplitState, RouteTable,
    ShardRoute, ShardRouter, ShardSplit, SplitEngineAdapter, SplitOrchestratorAdapter,
    SplitRuntime, SplitStateStore,
};

#[derive(Default)]
struct Engine {
    state: Option<DurableSplitState>,
    installs: usize,
    verified: usize,
}

impl SplitStateStore for Engine {
    type Error = ();
    fn load(&self) -> Result<Option<DurableSplitState>, Self::Error> {
        Ok(self.state.clone())
    }
    fn save(&mut self, state: &DurableSplitState) -> Result<(), Self::Error> {
        self.state = Some(state.clone());
        Ok(())
    }
    fn remove(&mut self) -> Result<(), Self::Error> {
        self.state = None;
        Ok(())
    }
}

impl AtomicSplitCutover for Engine {
    type Error = ();
    fn install_split_route(&mut self, _context: &CutoverContext, _state: &DurableSplitState) -> Result<u64, Self::Error> {
        self.installs += 1;
        Ok(self.installs as u64)
    }
    fn recover_split_route(
        &mut self,
        _split: &terrabase::shard_split::SplitContext,
    ) -> Result<Option<CommittedSplitRoute>, Self::Error> {
        Ok(None)
    }
}

impl SplitEngineAdapter for Engine {
    type DataError = ();
    fn create_split_target(
        &mut self,
        _context: &terrabase::shard_split::SplitContext,
    ) -> Result<(), Self::DataError> {
        Ok(())
    }
    fn rollback_split_target(
        &mut self,
        _context: &terrabase::shard_split::SplitContext,
    ) -> Result<(), Self::DataError> {
        Ok(())
    }
    fn verify_split_target(
        &mut self,
        _context: &terrabase::shard_split::SplitContext,
    ) -> Result<(), Self::DataError> {
        self.verified += 1;
        Ok(())
    }
    fn cleanup_split_source(
        &mut self,
        _context: &terrabase::shard_split::SplitContext,
    ) -> Result<(), Self::DataError> {
        Ok(())
    }
}

#[derive(Default)]
struct Orchestrator {
    validations: usize,
    publications: usize,
}
impl SplitOrchestratorAdapter for Orchestrator {
    type Error = ();
    fn validate_split_placement(
        &mut self,
        _incarnation: u128,
        _epoch: u64,
    ) -> Result<(), Self::Error> {
        self.validations += 1;
        Ok(())
    }
    fn publish_split_frontier(
        &mut self,
        _context: &terrabase::shard_split::SplitContext,
        _generation: u64,
    ) -> Result<(), Self::Error> {
        self.publications += 1;
        Ok(())
    }
}

#[test]
fn runtime_publishes_only_after_atomic_route_install() {
    let context = terrabase::shard_split::SplitContext::new(1, 2, 7, 8, 3, 4);
    let split = ShardSplit::plan_with_context(None, b"m".to_vec(), context).unwrap();
    let mut runtime =
        SplitRuntime::new(Engine::default(), Orchestrator::default(), ShardRouter::single(7));
    runtime.admit(split).unwrap();
    runtime.begin_catch_up(10).unwrap();
    runtime.note_frontiers(10, 10, 10).unwrap();
    let mut fence = terrabase::shard_split::WriterFence::new();
    fence.begin_drain(10).unwrap();
    fence.note_acked(10).unwrap();
    let permit = fence.try_fence(10).unwrap();
    assert_eq!(runtime.cut_over(permit, 10), Ok(1));
    assert_eq!(runtime.router().route(b"a"), Some(8));
    assert_eq!(runtime.router().route(b"m"), Some(7));
    runtime.complete().unwrap();
}

#[test]
fn runtime_reopens_same_operation_and_revalidates_placement() {
    let context = terrabase::shard_split::SplitContext::new(9, 10, 1, 2, 5, 0);
    let split = ShardSplit::plan_with_context(None, b"k".to_vec(), context).unwrap();
    let mut first =
        SplitRuntime::new(Engine::default(), Orchestrator::default(), ShardRouter::single(1));
    first.admit(split).unwrap();
    let mut engine = Engine::default();
    engine.state = first.split().map(ShardSplit::durable_state);
    let mut resumed = SplitRuntime::new(engine, Orchestrator::default(), ShardRouter::single(1));
    assert!(resumed.resume().unwrap());
    assert_eq!(resumed.split().unwrap().context().unwrap().split_id(), 9);
}

#[derive(Default)]
struct CrashShared {
    state: Option<DurableSplitState>,
    route: Option<CommittedSplitRoute>,
    fail_save: bool,
    publications: usize,
}

#[derive(Clone, Default)]
struct CrashEngine(Rc<RefCell<CrashShared>>);

impl SplitStateStore for CrashEngine {
    type Error = ();
    fn load(&self) -> Result<Option<DurableSplitState>, Self::Error> {
        Ok(self.0.borrow().state.clone())
    }
    fn save(&mut self, state: &DurableSplitState) -> Result<(), Self::Error> {
        let mut shared = self.0.borrow_mut();
        if shared.fail_save {
            shared.fail_save = false;
            return Err(());
        }
        shared.state = Some(state.clone());
        Ok(())
    }
    fn remove(&mut self) -> Result<(), Self::Error> {
        self.0.borrow_mut().state = None;
        Ok(())
    }
}

impl AtomicSplitCutover for CrashEngine {
    type Error = ();
    fn install_split_route(&mut self, context: &CutoverContext, _state: &DurableSplitState) -> Result<u64, Self::Error> {
        self.0.borrow_mut().route =
            Some(CommittedSplitRoute {
                split: context.split,
                generation: 1,
                route: RouteTable::new(vec![
                    ShardRoute::new(context.split.target_shard(), None, Some(b"k".to_vec())).unwrap(),
                    ShardRoute::new(context.split.source_shard(), Some(b"k".to_vec()), None).unwrap(),
                ]).unwrap(),
            });
        Ok(1)
    }
    fn recover_split_route(
        &mut self,
        _split: &terrabase::shard_split::SplitContext,
    ) -> Result<Option<CommittedSplitRoute>, Self::Error> {
        Ok(self.0.borrow().route.clone())
    }
}

impl SplitEngineAdapter for CrashEngine {
    type DataError = ();
    fn create_split_target(
        &mut self,
        _: &terrabase::shard_split::SplitContext,
    ) -> Result<(), Self::DataError> {
        Ok(())
    }
    fn rollback_split_target(
        &mut self,
        _: &terrabase::shard_split::SplitContext,
    ) -> Result<(), Self::DataError> {
        Ok(())
    }
    fn verify_split_target(
        &mut self,
        _: &terrabase::shard_split::SplitContext,
    ) -> Result<(), Self::DataError> {
        Ok(())
    }
    fn cleanup_split_source(
        &mut self,
        _: &terrabase::shard_split::SplitContext,
    ) -> Result<(), Self::DataError> {
        Ok(())
    }
}

#[derive(Clone, Default)]
struct CrashOrchestrator(Rc<RefCell<CrashShared>>);
impl SplitOrchestratorAdapter for CrashOrchestrator {
    type Error = ();
    fn validate_split_placement(&mut self, _: u128, _: u64) -> Result<(), Self::Error> {
        Ok(())
    }
    fn publish_split_frontier(
        &mut self,
        _: &terrabase::shard_split::SplitContext,
        _: u64,
    ) -> Result<(), Self::Error> {
        self.0.borrow_mut().publications += 1;
        Ok(())
    }
}

#[test]
fn crash_after_atomic_install_recovers_committed_routes_without_stale_reads() {
    let shared = Rc::new(RefCell::new(CrashShared::default()));
    let context = terrabase::shard_split::SplitContext::new(11, 12, 7, 8, 3, 0);
    let split = ShardSplit::plan_with_context(None, b"m".to_vec(), context).unwrap();
    let engine = CrashEngine(shared.clone());
    let orchestrator = CrashOrchestrator(shared.clone());
    let mut runtime = SplitRuntime::new(engine, orchestrator, ShardRouter::single(7));
    runtime.admit(split).unwrap();
    runtime.begin_catch_up(4).unwrap();
    runtime.note_frontiers(4, 4, 4).unwrap();
    let mut fence = terrabase::shard_split::WriterFence::new();
    fence.begin_drain(4).unwrap();
    fence.note_acked(4).unwrap();
    let permit = fence.try_fence(4).unwrap();
    shared.borrow_mut().fail_save = true;
    assert!(runtime.cut_over(permit, 4).is_err());
    assert_eq!(shared.borrow().publications, 0);
    assert_eq!(shared.borrow().route.unwrap().generation, 1);

    let routes = RouteTable::new(vec![
        ShardRoute::new(8, None, Some(b"m".to_vec())).unwrap(),
        ShardRoute::new(7, Some(b"m".to_vec()), None).unwrap(),
    ])
    .unwrap();
    let mut restarted = SplitRuntime::new(
        CrashEngine(shared.clone()),
        CrashOrchestrator(shared.clone()),
        ShardRouter::from_table(routes),
    );
    assert!(restarted.resume().unwrap());
    assert_eq!(restarted.router().route(b"a"), Some(8));
    assert_eq!(restarted.router().route(b"m"), Some(7));
    assert_eq!(restarted.router().route(b"z"), Some(7));
    assert_eq!(restarted.split().unwrap().phase(), terrabase::shard_split::SplitPhase::CatchingUp);
}
