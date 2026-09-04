use std::collections::BTreeSet;

use super::*;

#[derive(Default)]
struct Adapter {
    events: Vec<LifecycleEvent>,
    visible: BTreeSet<String>,
    dropped: BTreeSet<String>,
}

impl StorageAdapter for Adapter {
    fn load_lifecycle(&mut self) -> Result<Vec<LifecycleEvent>, HookError> { Ok(self.events.clone()) }
    fn append_lifecycle(&mut self, event: &LifecycleEvent) -> Result<(), HookError> { self.events.push(event.clone()); Ok(()) }
    fn backfill_batch(&mut self, _index: &str, _first: u64, _last: u64) -> Result<(), HookError> { Ok(()) }
    fn catch_up_and_verify(&mut self, _index: &str, _covered: u64, target: u64) -> Result<u64, HookError> { Ok(target) }
    fn set_visible(&mut self, index: &str, visible: bool) -> Result<(), HookError> { if visible { self.visible.insert(index.to_owned()); } else { self.visible.remove(index); } Ok(()) }
    fn drop_physical(&mut self, index: &str) -> Result<(), HookError> { self.dropped.insert(index.to_owned()); Ok(()) }
    fn set_plan_pin(&mut self, _template: &str, _index: &str, _pinned: bool) -> Result<(), HookError> { Ok(()) }
    fn record_logical_cost(&mut self, _template: &Template, _cost: LogicalCost) -> Result<(), HookError> { Ok(()) }
    fn compile_hypothetical(&mut self, _template: &Template, _candidate: &Candidate) -> Result<bool, HookError> { Ok(true) }
}

fn digest() -> [u8; 32] { [7; 32] }

#[test]
fn compression_keeps_heaviest_prefix_per_collection() {
    let template = |field: &str| Template { collection: "users".into(), predicates: vec![Predicate { field: field.into(), kind: PredicateKind::Equality }], ordering: vec![], projection: None };
    let mut sketch = WorkloadSketch::new(32);
    for (field, count) in [("id", 19), ("email", 1)] {
        for _ in 0..count { sketch.observe(Observation { template: template(field), cost: LogicalCost::new(0, 10), rows_returned: 1, index: None, at_millis: 100 }); }
    }
    let compressed = sketch.compress(100, 1000, 0.95);
    assert_eq!(compressed.len(), 1);
    assert_eq!(compressed[0].template.predicates[0].field, "id");
}

#[test]
fn fts_candidate_is_not_silently_rewritten_to_btree() {
    let template = Template { collection: "docs".into(), predicates: vec![Predicate { field: "body".into(), kind: PredicateKind::FullText }], ordering: vec![], projection: None };
    let mut fields = BTreeSet::new(); fields.insert("body".into());
    let candidates = derive_candidates(&template, &fields);
    assert_eq!(candidates.len(), 1);
    assert_eq!(candidates[0].kind, CandidateKind::Fts);
}

#[test]
fn budget_rejects_nan_and_over_cap() {
    let budget = WriteBudget { base_cost: f64::NAN, maintenance_cost: 0.0, visible_indexes: 0 };
    assert!(!budget.allows(0.0));
    let budget = WriteBudget { base_cost: 1.0, maintenance_cost: 2.0, visible_indexes: 0 };
    assert!(!budget.allows(0.1));
}

#[test]
fn shadow_regression_gets_pinned_and_not_activated_unconditionally() {
    let baseline = vec![LogicalCost::new(1, 10); MIN_SHADOW_SAMPLES];
    let candidate = vec![LogicalCost::new(1, 12); MIN_SHADOW_SAMPLES];
    let (outcome, evidence) = verify_shadow(&baseline, &candidate).expect("bounded evidence");
    assert!(evidence.regression);
    assert_eq!(outcome, ShadowOutcome::ActivateWithPins);
}

#[test]
fn lifecycle_requires_durable_hooks_and_monotonic_watermarks() {
    let missing = HookAvailability { journal: true, ..HookAvailability::default() };
    assert_eq!(missing.validate(), Err(CoordinatorError::MissingHook(HookKind::PublicationTransaction)));
    let hooks = HookAvailability { journal: true, publication_transaction: true, planner: true, logical_cost: true, backfill: true };
    let mut coordinator = Coordinator::recover_with_hooks(Adapter::default(), hooks).expect("recovery");
    coordinator.append(LifecycleEvent { sequence: 0, index: "i".into(), phase: Phase::Selected, watermark: None, target: None, evidence_digest: digest(), at_millis: 1 }).expect("select");
    coordinator.start_build("i", 10, 2, digest()).expect("build");
    coordinator.advance_backfill("i", 5, 3, digest()).expect("first batch");
    assert_eq!(coordinator.advance_backfill("i", 4, 4, digest()), Err(CoordinatorError::InvalidWatermark));
    coordinator.advance_backfill("i", 10, 5, digest()).expect("second batch");
    coordinator.begin_shadow("i", 6, digest()).expect("shadow");
    coordinator.activate("i", 7, digest()).expect("atomic activate");
    assert_eq!(coordinator.state("i").map(|s| s.phase), Some(Phase::Active));
}

#[test]
fn rollback_is_immediate_but_drop_waits_for_grace_period() {
    let hooks = HookAvailability { journal: true, publication_transaction: true, planner: true, logical_cost: true, backfill: true };
    let adapter = Adapter::default();
    let mut coordinator = Coordinator::recover_with_hooks(adapter, hooks).expect("recovery");
    coordinator.append(LifecycleEvent { sequence: 0, index: "i".into(), phase: Phase::Selected, watermark: None, target: None, evidence_digest: digest(), at_millis: 1 }).expect("select");
    coordinator.start_build("i", 2, 2, digest()).expect("build");
    coordinator.advance_backfill("i", 2, 3, digest()).expect("backfill");
    coordinator.begin_shadow("i", 4, digest()).expect("shadow");
    coordinator.activate("i", 5, digest()).expect("active");
    coordinator.rollback("i", 6, digest()).expect("revert");
    assert_eq!(coordinator.retire("i", 2, digest()), Err(CoordinatorError::InvalidTransition));
    coordinator.retire("i", DROP_GRACE_MILLIS + 6, digest()).expect("delayed drop");
    assert!(coordinator.adapter_mut().dropped.contains("i"));
}
