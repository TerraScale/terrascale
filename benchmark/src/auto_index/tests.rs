use std::collections::BTreeSet;

use super::*;

#[derive(Default)]
struct Adapter {
    events: Vec<LifecycleEvent>,
    refuse_append: bool,
    visible: BTreeSet<String>,
    dropped: BTreeSet<String>,
}

impl StorageAdapter for Adapter {
    fn load_lifecycle(&mut self) -> Result<Vec<LifecycleEvent>, HookError> {
        Ok(self.events.clone())
    }
    fn append_lifecycle(&mut self, event: &LifecycleEvent) -> Result<(), HookError> {
        if self.refuse_append {
            return Err(HookError::Refused);
        }
        self.events.push(event.clone());
        Ok(())
    }
    fn backfill_batch(&mut self, _index: &str, _first: u64, _last: u64) -> Result<(), HookError> {
        Ok(())
    }
    fn catch_up_and_verify(
        &mut self,
        _index: &str,
        _covered: u64,
        target: u64,
    ) -> Result<u64, HookError> {
        Ok(target)
    }
    fn set_visible(&mut self, index: &str, visible: bool) -> Result<(), HookError> {
        if visible {
            self.visible.insert(index.to_owned());
        } else {
            self.visible.remove(index);
        }
        Ok(())
    }
    fn drop_physical(&mut self, index: &str) -> Result<(), HookError> {
        self.dropped.insert(index.to_owned());
        Ok(())
    }
    fn set_plan_pin(
        &mut self,
        _template: &str,
        _index: &str,
        _pinned: bool,
    ) -> Result<(), HookError> {
        Ok(())
    }
    fn record_logical_cost(
        &mut self,
        _template: &Template,
        _cost: LogicalCost,
    ) -> Result<(), HookError> {
        Ok(())
    }
    fn compile_hypothetical(
        &mut self,
        _template: &Template,
        _candidate: &Candidate,
    ) -> Result<bool, HookError> {
        Ok(true)
    }
}

fn digest() -> [u8; 32] {
    [7; 32]
}

#[test]
fn compression_keeps_heaviest_prefix_per_collection() {
    let template = |field: &str| Template {
        collection: "users".into(),
        predicates: vec![Predicate { field: field.into(), kind: PredicateKind::Equality }],
        ordering: vec![],
        projection: None,
    };
    let mut sketch = WorkloadSketch::new(32);
    for (field, count) in [("id", 19), ("email", 1)] {
        for _ in 0..count {
            sketch.observe(Observation {
                tenant: "tenant-a".into(),
                template: template(field),
                cost: LogicalCost::new(0, 10),
                rows_returned: 1,
                index: None,
                at_millis: 100,
            });
        }
    }
    sketch.observe(Observation {
        tenant: "tenant-b".into(),
        template: template("email"),
        cost: LogicalCost::new(0, 1_000_000),
        rows_returned: 1,
        index: None,
        at_millis: 100,
    });
    let compressed = sketch.compress("tenant-a", 100, 1000, 0.95);
    assert_eq!(compressed.len(), 1);
    assert_eq!(compressed[0].template.predicates[0].field, "id");
    let other = sketch.compress("tenant-b", 100, 1000, 0.95);
    assert_eq!(other.len(), 1);
    assert_eq!(other[0].template.predicates[0].field, "email");
}

#[test]
fn fts_candidate_is_not_silently_rewritten_to_btree() {
    let template = Template {
        collection: "docs".into(),
        predicates: vec![Predicate { field: "body".into(), kind: PredicateKind::FullText }],
        ordering: vec![],
        projection: None,
    };
    let mut fields = BTreeSet::new();
    fields.insert("body".into());
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
    assert_eq!(outcome, ShadowOutcome::Abandon);
}

#[test]
fn shadow_guards_reject_component_and_tail_regressions() {
    for (baseline, candidate) in [
        (LogicalCost::new(1, 99), LogicalCost::new(2, 48)),
        (LogicalCost::new(99, 1), LogicalCost::new(48, 2)),
        (LogicalCost::new(0, 100), LogicalCost::new(1, 49)),
    ] {
        let (outcome, evidence) = verify_shadow(
            &vec![baseline; MIN_SHADOW_SAMPLES],
            &vec![candidate; MIN_SHADOW_SAMPLES],
        )
        .unwrap();
        assert!(evidence.improvement > MIN_IMPROVEMENT_FRACTION);
        assert!(evidence.regression);
        assert_eq!(outcome, ShadowOutcome::Abandon);
    }
    // Nearest-rank p95 of 22 samples includes the second-largest sample.
    // Flooring (n - 1) * 0.95 would miss both expensive executions here.
    let baseline = vec![LogicalCost::new(0, 100); 22];
    let mut candidate = vec![LogicalCost::new(0, 50); 22];
    candidate[20] = LogicalCost::new(0, 106);
    candidate[21] = LogicalCost::new(0, 106);
    let (outcome, evidence) = verify_shadow(&baseline, &candidate).unwrap();
    assert!(evidence.improvement > MIN_IMPROVEMENT_FRACTION);
    assert!(evidence.regression);
    assert_eq!(outcome, ShadowOutcome::Abandon);
}

#[test]
fn shadow_guards_are_exact_and_measurements_are_bounded() {
    // Above f64's exact-integer range, one extra unit still crosses the guard.
    let base = ((1_u64 << 54) / 100) * 100;
    let threshold = base / 100 * 105;
    let baseline = vec![LogicalCost::new(0, base); MIN_SHADOW_SAMPLES];
    let equal = vec![LogicalCost::new(0, threshold); MIN_SHADOW_SAMPLES];
    assert!(!verify_shadow(&baseline, &equal).unwrap().1.regression);
    let over = vec![LogicalCost::new(0, threshold + 1); MIN_SHADOW_SAMPLES];
    assert!(verify_shadow(&baseline, &over).unwrap().1.regression);
    for samples in [
        vec![LogicalCost::new(1, 1); MIN_SHADOW_SAMPLES - 1],
        vec![LogicalCost::new(1, 1); MAX_SHADOW_SAMPLES + 1],
        vec![LogicalCost::new(u64::MAX, 1); MIN_SHADOW_SAMPLES],
    ] {
        assert_eq!(verify_shadow(&samples, &baseline), Err(CoordinatorError::InsufficientEvidence));
        assert_eq!(verify_shadow(&baseline, &samples), Err(CoordinatorError::InsufficientEvidence));
    }
    let maximum = vec![LogicalCost::new(0, u64::MAX); MAX_SHADOW_SAMPLES];
    let (outcome, evidence) = verify_shadow(&maximum, &maximum).unwrap();
    assert_eq!(outcome, ShadowOutcome::Abandon);
    assert!(!evidence.regression);
}

#[test]
fn hybrid_plan_is_disjoint_and_bounded() {
    let state = IndexState {
        id: "i".into(),
        collection: "c".into(),
        phase: Phase::Building,
        target: 100,
        covered: 40,
        visible: false,
        generation: 1,
        last_used_millis: 0,
        invisible_since_millis: None,
    };
    assert_eq!(
        state.hybrid_plan(60),
        Some(HybridPlan { index_through: 40, scan_from: 41, scan_through: 100 })
    );
    assert_eq!(state.hybrid_plan(59), None);
}

#[test]
fn recovery_refuses_reordered_journal_events() {
    let hooks = HookAvailability {
        journal: true,
        publication_transaction: true,
        planner: true,
        logical_cost: true,
        backfill: true,
    };
    let events = vec![LifecycleEvent {
        sequence: 2,
        index: "i".into(),
        phase: Phase::Selected,
        watermark: None,
        target: None,
        evidence_digest: digest(),
        at_millis: 1,
    }];
    let adapter = Adapter { events, ..Adapter::default() };
    assert_eq!(
        Coordinator::recover_with_hooks(adapter, hooks).err(),
        Some(CoordinatorError::JournalConflict)
    );
}

#[test]
fn lifecycle_requires_durable_hooks_and_monotonic_watermarks() {
    assert!(matches!(
        Coordinator::recover(Adapter::default()),
        Err(CoordinatorError::MissingHook(HookKind::Journal))
    ));
    let missing = HookAvailability { journal: true, ..HookAvailability::default() };
    assert_eq!(
        missing.validate(),
        Err(CoordinatorError::MissingHook(HookKind::PublicationTransaction))
    );
    let hooks = HookAvailability {
        journal: true,
        publication_transaction: true,
        planner: true,
        logical_cost: true,
        backfill: true,
    };
    let mut coordinator =
        Coordinator::recover_with_hooks(Adapter::default(), hooks).expect("recovery");
    coordinator
        .append(LifecycleEvent {
            sequence: 0,
            index: "i".into(),
            phase: Phase::Selected,
            watermark: None,
            target: None,
            evidence_digest: digest(),
            at_millis: 1,
        })
        .expect("select");
    coordinator.start_build("i", 10, 2, digest()).expect("build");
    coordinator.advance_backfill("i", 5, 3, digest()).expect("first batch");
    assert_eq!(
        coordinator.advance_backfill("i", 4, 4, digest()),
        Err(CoordinatorError::InvalidWatermark)
    );
    coordinator.advance_backfill("i", 10, 5, digest()).expect("second batch");
    coordinator.begin_shadow("i", 6, digest()).expect("shadow");
    coordinator.activate("i", 7, digest()).expect("atomic activate");
    assert_eq!(coordinator.state("i").map(|s| s.phase), Some(Phase::Active));
}

#[test]
fn rejected_lifecycle_events_leave_replay_and_sequence_unchanged() {
    let hooks = HookAvailability {
        journal: true,
        publication_transaction: true,
        planner: true,
        logical_cost: true,
        backfill: true,
    };
    let mut coordinator = Coordinator::recover_with_hooks(Adapter::default(), hooks).unwrap();
    assert_eq!(
        coordinator.start_build("missing", 10, 1, digest()),
        Err(CoordinatorError::InvalidTransition)
    );
    assert!(coordinator.states.is_empty());
    assert!(coordinator.adapter.events.is_empty());
    let selected = LifecycleEvent {
        sequence: 0,
        index: "i".into(),
        phase: Phase::Selected,
        watermark: None,
        target: None,
        evidence_digest: digest(),
        at_millis: 1,
    };
    coordinator.append(selected.clone()).unwrap();
    coordinator.start_build("i", 10, 2, digest()).unwrap();
    coordinator.advance_backfill("i", 5, 3, digest()).unwrap();
    let before = coordinator.states.clone();
    let sequence = coordinator.next_sequence;
    let event_count = coordinator.adapter.events.len();
    for (phase, watermark, target, expected) in [
        (Phase::Active, None, None, CoordinatorError::InvalidTransition),
        (Phase::Building, Some(4), Some(20), CoordinatorError::InvalidWatermark),
        (Phase::Building, Some(11), None, CoordinatorError::InvalidWatermark),
        (Phase::Shadowing, Some(5), None, CoordinatorError::InvalidWatermark),
        (Phase::Shadowing, Some(10), None, CoordinatorError::InvalidWatermark),
    ] {
        let event = LifecycleEvent { phase, watermark, target, ..selected.clone() };
        assert_eq!(coordinator.append(event.clone()), Err(expected));
        assert_eq!(coordinator.states, before);
        assert_eq!(coordinator.next_sequence, sequence);
        assert_eq!(coordinator.adapter.events.len(), event_count);
        // Recovery validation uses the same preparation and must also be atomic.
        assert_eq!(coordinator.apply_event(&LifecycleEvent { sequence, ..event }), Err(expected));
        assert_eq!(coordinator.states, before);
        assert_eq!(coordinator.next_sequence, sequence);
    }
    coordinator.adapter.refuse_append = true;
    let progress =
        LifecycleEvent { phase: Phase::Building, watermark: Some(8), ..selected.clone() };
    assert_eq!(
        coordinator.append(progress.clone()),
        Err(CoordinatorError::Hook(HookError::Refused))
    );
    assert_eq!(coordinator.states, before);
    assert_eq!(coordinator.next_sequence, sequence);
    coordinator.adapter.refuse_append = false;
    coordinator.append(progress).unwrap();
    assert_eq!(coordinator.adapter.events.last().unwrap().sequence, sequence);
    let recovered = Coordinator::recover_with_hooks(
        Adapter { events: coordinator.adapter.events.clone(), ..Adapter::default() },
        hooks,
    )
    .unwrap();
    assert_eq!(recovered.states, coordinator.states);
    assert_eq!(recovered.next_sequence, coordinator.next_sequence);
    // Sequence exhaustion is rejected before durable append as well.
    coordinator.next_sequence = u64::MAX;
    let event_count = coordinator.adapter.events.len();
    assert_eq!(coordinator.append(selected), Err(CoordinatorError::JournalConflict));
    assert_eq!(coordinator.adapter.events.len(), event_count);
    assert_eq!(coordinator.states, recovered.states);
}

#[test]
fn rollback_is_immediate_but_drop_waits_for_grace_period() {
    let hooks = HookAvailability {
        journal: true,
        publication_transaction: true,
        planner: true,
        logical_cost: true,
        backfill: true,
    };
    let adapter = Adapter::default();
    let mut coordinator = Coordinator::recover_with_hooks(adapter, hooks).expect("recovery");
    coordinator
        .append(LifecycleEvent {
            sequence: 0,
            index: "i".into(),
            phase: Phase::Selected,
            watermark: None,
            target: None,
            evidence_digest: digest(),
            at_millis: 1,
        })
        .expect("select");
    coordinator.start_build("i", 2, 2, digest()).expect("build");
    coordinator.advance_backfill("i", 2, 3, digest()).expect("backfill");
    coordinator.begin_shadow("i", 4, digest()).expect("shadow");
    coordinator.activate("i", 5, digest()).expect("active");
    coordinator.rollback("i", 6, digest()).expect("revert");
    assert_eq!(coordinator.retire("i", 2, digest()), Err(CoordinatorError::InvalidTransition));
    coordinator.retire("i", DROP_GRACE_MILLIS + 6, digest()).expect("delayed drop");
    assert!(coordinator.adapter_mut().dropped.contains("i"));
    let event_count = coordinator.adapter.events.len();
    assert_eq!(
        coordinator.retire("i", DROP_GRACE_MILLIS + 7, digest()),
        Err(CoordinatorError::InvalidTransition)
    );
    assert_eq!(coordinator.adapter.events.len(), event_count);
}
