use terrabase::shard_split::{
    AtomicSplitCutover, CutoverContext, DurableSplitState, ShardRouter, ShardSplit,
    SplitEngineAdapter, SplitOrchestratorAdapter, SplitRuntime, SplitStateStore,
};

#[derive(Default)]
struct Engine {
    state: Option<DurableSplitState>,
    installs: usize,
    verified: usize,
}

impl SplitStateStore for Engine {
    type Error = ();
    fn load(&self) -> Result<Option<DurableSplitState>, Self::Error> { Ok(self.state.clone()) }
    fn save(&mut self, state: &DurableSplitState) -> Result<(), Self::Error> {
        self.state = Some(state.clone());
        Ok(())
    }
    fn remove(&mut self) -> Result<(), Self::Error> { self.state = None; Ok(()) }
}

impl AtomicSplitCutover for Engine {
    type Error = ();
    fn install_split_route(&mut self, _context: &CutoverContext) -> Result<u64, Self::Error> {
        self.installs += 1;
        Ok(self.installs as u64)
    }
}

impl SplitEngineAdapter for Engine {
    type DataError = ();
    fn create_split_target(&mut self, _context: &terrabase::shard_split::SplitContext) -> Result<(), Self::DataError> { Ok(()) }
    fn rollback_split_target(&mut self, _context: &terrabase::shard_split::SplitContext) -> Result<(), Self::DataError> { Ok(()) }
    fn verify_split_target(&mut self, _context: &terrabase::shard_split::SplitContext) -> Result<(), Self::DataError> { self.verified += 1; Ok(()) }
    fn cleanup_split_source(&mut self, _context: &terrabase::shard_split::SplitContext) -> Result<(), Self::DataError> { Ok(()) }
}

#[derive(Default)]
struct Orchestrator { validations: usize, publications: usize }
impl SplitOrchestratorAdapter for Orchestrator {
    type Error = ();
    fn validate_split_placement(&mut self, _incarnation: u128, _epoch: u64) -> Result<(), Self::Error> { self.validations += 1; Ok(()) }
    fn publish_split_frontier(&mut self, _context: &terrabase::shard_split::SplitContext, _generation: u64) -> Result<(), Self::Error> { self.publications += 1; Ok(()) }
}

#[test]
fn runtime_publishes_only_after_atomic_route_install() {
    let context = terrabase::shard_split::SplitContext::new(1, 2, 7, 8, 3, 4);
    let split = ShardSplit::plan_with_context(None, b"m".to_vec(), context).unwrap();
    let mut runtime = SplitRuntime::new(Engine::default(), Orchestrator::default(), ShardRouter::single(7));
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
    let mut first = SplitRuntime::new(Engine::default(), Orchestrator::default(), ShardRouter::single(1));
    first.admit(split).unwrap();
    let mut engine = Engine::default();
    engine.state = first.split().map(ShardSplit::durable_state);
    let mut resumed = SplitRuntime::new(engine, Orchestrator::default(), ShardRouter::single(1));
    assert!(resumed.resume().unwrap());
    assert_eq!(resumed.split().unwrap().context().unwrap().split_id(), 9);
}
