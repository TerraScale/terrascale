//! In-crate engine-seam tests of the GAP-Q18 index lifecycle contract.
//!
//! Every test drives the typed surface through
//! [`Engine::indexes`](crate::engine::Engine::indexes): the state machine
//! and its exhaustive transition matrix, per-state staleness, the bounded
//! wait-for-non-stale, lock modes, both reset modes, the recorded-error
//! contract, and the statistics surface. The decision record is
//! `docs/index/gapq18-lifecycle-staleness.md`; V1 anchors cite the pinned
//! read-only V1 tree from there.

use std::{
    path::PathBuf,
    sync::atomic::{AtomicU64, Ordering},
    time::Duration,
};

use super::operations::{
    INDEX_ERROR_STATE_THRESHOLD, IndexDeployment, IndexErrorAction, IndexErrorCategory,
    IndexLockMode, IndexOperationError, IndexOperationalState, IndexPriority, IndexResetMode,
    IndexStatistics, IndexStore, MAX_INDEX_ERRORS_PER_INDEX, WaitForNonStale,
};
use crate::{
    engine::{Engine, EngineConfig},
    operations::QueryInput,
    query::GenerationView,
    query::optimizer::{IndexCapabilities, IndexDefinition, IndexField, IndexKind},
    query::{
        IndexNamespace, ObservedGeneration, QueryError, QueryOrdering, QueryPaging, QueryPredicate,
        QueryProjection, QuerySource, QuerySpec, StalenessMode, StalenessPolicy,
    },
    types::{
        Cancellation, CollectionName, Deadline, Document, DocumentId, DocumentValue,
        ExpectedVersion, LocalDurability, OperationId, TenantId, TenantScope,
    },
};

fn location(tag: &str) -> PathBuf {
    static COUNTER: AtomicU64 = AtomicU64::new(0);
    let index = COUNTER.fetch_add(1, Ordering::Relaxed);
    std::env::temp_dir().join(format!("tbv2-gapq18-{tag}-{}-{index}", std::process::id()))
}

fn open_engine(tag: &str) -> (PathBuf, Engine) {
    let dir = location(tag);
    std::fs::remove_dir_all(&dir).ok();
    let engine =
        Engine::open(EngineConfig::durable(&dir).expect("durable location")).expect("engine opens");
    (dir, engine)
}

fn tenant(name: &str) -> TenantId {
    TenantId::try_new(name).expect("valid tenant")
}

fn scope(engine: &Engine, name: &str) -> TenantScope {
    TenantScope::from_parts(tenant(name), engine.identity())
}

fn namespace(engine_tenant: &str, name: &str) -> IndexNamespace {
    IndexNamespace::try_new(tenant(engine_tenant), name).expect("valid namespace")
}

fn object_document(id: &str, collection: &str, fields: &[(&str, DocumentValue)]) -> Document {
    Document::new(
        DocumentId::try_new(id).expect("valid identifier"),
        CollectionName::try_new(collection).expect("valid identifier"),
        DocumentValue::Object(
            fields
                .iter()
                .map(|(name, value)| ((*name).to_owned(), value.clone()))
                .collect::<std::collections::BTreeMap<_, _>>(),
        ),
    )
}

fn commit_one(engine: &Engine, tenant_name: &str, tag: &str, id: &str, age: i64) {
    let tenant_scope = scope(engine, tenant_name);
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let operation = OperationId::try_new(tag).expect("valid identifier");
    let mut transaction = engine.begin(&tenant_scope, snapshot, operation).expect("begins");
    transaction
        .put(
            object_document(id, "users", &[("age", DocumentValue::Integer(age))]),
            ExpectedVersion::Any,
        )
        .expect("puts");
    let outcome = transaction.commit(LocalDurability::Synced);
    assert!(matches!(outcome, crate::engine::CommitOutcome::Committed(_)), "commit lands");
}

fn field(name: &str) -> IndexField {
    IndexField::try_new(name, IndexCapabilities::FULL).expect("valid field")
}

fn age_definition() -> IndexDefinition {
    IndexDefinition::try_new(
        CollectionName::try_new("users").expect("valid identifier"),
        vec![field("age")],
        IndexKind::Explicit,
    )
    .expect("valid definition")
}

fn widened_definition() -> IndexDefinition {
    IndexDefinition::try_new(
        CollectionName::try_new("users").expect("valid identifier"),
        vec![field("age"), field("name")],
        IndexKind::Explicit,
    )
    .expect("valid definition")
}

fn auto_definition() -> IndexDefinition {
    IndexDefinition::try_new(
        CollectionName::try_new("users").expect("valid identifier"),
        vec![field("age")],
        IndexKind::Auto,
    )
    .expect("valid definition")
}

/// Creates one explicit index under a fresh name and returns its namespace.
fn create_explicit(store: &IndexStore<'_>, tenant_name: &str, name: &str) -> IndexNamespace {
    let ns = namespace(tenant_name, name);
    store.create(&ns, &age_definition(), IndexDeployment::DEFAULT).expect("explicit index creates");
    ns
}

/// Drives the namespace into the requested operational state.
fn reach(
    store: &IndexStore<'_>,
    catalog: &super::IndexCatalog,
    ns: &IndexNamespace,
    state: IndexOperationalState,
) {
    match state {
        IndexOperationalState::Normal => {}
        IndexOperationalState::Paused => {
            store.stop(ns).expect("pause lands");
        }
        IndexOperationalState::Disabled => {
            store.disable(ns).expect("disable lands");
        }
        IndexOperationalState::Error => {
            for _ in 0..INDEX_ERROR_STATE_THRESHOLD {
                catalog.note_operation_failure(
                    ns,
                    IndexErrorAction::Build,
                    IndexErrorCategory::StorageFailure,
                );
            }
            assert_eq!(store.state(ns), Ok(IndexOperationalState::Error), "fault enters error");
        }
    }
}

/// One query spec naming one required namespace under one staleness mode.
fn index_query(tenant_name: &str, required: &IndexNamespace, mode: StalenessMode) -> QuerySpec {
    QuerySpec::try_new(
        tenant(tenant_name),
        QuerySource::try_new(CollectionName::try_new("users").expect("valid identifier"))
            .expect("user collection"),
        QueryPredicate::all(Vec::new()).expect("valid conjunction"),
        QueryOrdering::default(),
        QueryProjection::whole_document(),
        QueryPaging::try_new(0, None).expect("valid paging"),
        StalenessPolicy::try_new(mode, vec![required.clone()]).expect("valid policy"),
        Deadline::after(Duration::from_secs(30)).expect("valid deadline"),
        Cancellation::new(),
    )
    .expect("valid specification")
}

/// Unwraps a published rebuild step into its epoch and generation.
fn expect_published(step: super::operations::RebuildStepOutcome) -> (u64, u64) {
    match step {
        super::operations::RebuildStepOutcome::Published { epoch, generation, .. } => {
            (epoch, generation)
        }
        other => panic!("expected a published step, got {other:?}"),
    }
}

// ---------------------------------------------------------------------------
// Creation, locking, deletion
// ---------------------------------------------------------------------------

#[test]
fn create_registers_an_explicit_index_with_deployment_options() {
    let (dir, engine) = open_engine("create-basic");
    let store = engine.indexes();
    let ns = namespace("tenant-a", "by-age");
    let outcome = store
        .create(
            &ns,
            &age_definition(),
            IndexDeployment::new(IndexPriority::High, IndexLockMode::Unlock),
        )
        .expect("explicit index creates");
    assert!(matches!(outcome, super::operations::IndexCreationOutcome::Created { epoch: 1 }));
    assert_eq!(store.state(&ns), Ok(IndexOperationalState::Normal));
    let statistics = store.statistics(&tenant("tenant-a"));
    assert_eq!(statistics.len(), 1, "one explicit index of the tenant");
    assert_eq!(statistics[0].name(), "by-age");
    assert_eq!(statistics[0].priority(), IndexPriority::High);
    assert_eq!(statistics[0].lock_mode(), IndexLockMode::Unlock);
    // A second create with identical content is not a definition change.
    let again = store
        .create(
            &ns,
            &age_definition(),
            IndexDeployment::new(IndexPriority::High, IndexLockMode::Unlock),
        )
        .expect("identical content is accepted under any lock");
    assert!(matches!(again, super::operations::IndexCreationOutcome::Unchanged { epoch: 1 }));
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn create_refuses_auto_namespaces_and_non_explicit_definitions() {
    let (dir, engine) = open_engine("create-refusals");
    let store = engine.indexes();
    let auto_named = namespace("tenant-a", "Auto/users/ByAge");
    assert_eq!(
        store.create(&auto_named, &age_definition(), IndexDeployment::DEFAULT),
        Err(IndexOperationError::AutoNamespaceReserved),
        "the reserved auto namespace refuses explicit creation"
    );
    assert_eq!(
        store.create(
            &namespace("tenant-a", "by-age"),
            &auto_definition(),
            IndexDeployment::DEFAULT
        ),
        Err(IndexOperationError::KindMismatch),
        "an auto-kind definition cannot register explicitly"
    );
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn lock_modes_guard_definition_changes_exactly_as_v1() {
    let (dir, engine) = open_engine("lock-modes");
    let store = engine.indexes();
    let catalog = engine.index_catalog();

    // Unlock: a definition change updates in place.
    let unlocked = create_explicit(&store, "tenant-a", "by-age");
    let updated = store
        .create(&unlocked, &widened_definition(), IndexDeployment::DEFAULT)
        .expect("unlock accepts definition changes");
    assert!(
        matches!(updated, super::operations::IndexCreationOutcome::Updated { epoch: 1 }),
        "the namespace epoch is preserved across an update"
    );
    let view = catalog.definitions(&tenant("tenant-a"));
    assert_eq!(view[0].definition().fields().len(), 2, "the widened definition landed");

    // LockedIgnore: a definition change is silently ignored.
    let ignored = create_explicit(&store, "tenant-a", "locked-ignore");
    store.set_lock(&ignored, IndexLockMode::LockedIgnore).expect("lock mode changes");
    let outcome = store
        .create(&ignored, &widened_definition(), IndexDeployment::DEFAULT)
        .expect("locked-ignore does not fail the call");
    assert!(matches!(outcome, super::operations::IndexCreationOutcome::Ignored { epoch: 1 }));
    let entry = catalog
        .definitions(&tenant("tenant-a"))
        .into_iter()
        .find(|entry| entry.namespace().name() == "locked-ignore")
        .expect("still registered");
    assert_eq!(entry.definition().fields().len(), 1, "the original definition stands");

    // LockedError: a definition change fails closed.
    let refused = create_explicit(&store, "tenant-a", "locked-error");
    store.set_lock(&refused, IndexLockMode::LockedError).expect("lock mode changes");
    assert_eq!(
        store.create(&refused, &widened_definition(), IndexDeployment::DEFAULT),
        Err(IndexOperationError::DefinitionLocked)
    );
    let entry = catalog
        .definitions(&tenant("tenant-a"))
        .into_iter()
        .find(|entry| entry.namespace().name() == "locked-error")
        .expect("still registered");
    assert_eq!(entry.definition().fields().len(), 1, "the original definition stands");
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn delete_removes_an_explicit_index_and_keeps_a_tombstone() {
    let (dir, engine) = open_engine("delete-explicit");
    let store = engine.indexes();
    let catalog = engine.index_catalog();
    let ns = create_explicit(&store, "tenant-a", "by-age");
    store.delete(&ns).expect("explicit index deletes");
    assert_eq!(store.state(&ns), Err(IndexOperationError::NamespaceUnknown));
    assert!(
        store.statistics(&tenant("tenant-a")).is_empty(),
        "no statistics remain for a deleted index"
    );
    let retired = catalog.retired_records(&tenant("tenant-a"));
    assert_eq!(retired.len(), 1, "a bounded tombstone keeps the retirement observable");
    assert_eq!(retired[0].name(), "by-age");
    assert_eq!(retired[0].kind(), IndexKind::Explicit);
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn delete_refuses_unknown_namespaces_and_auto_indexes() {
    let (dir, engine) = open_engine("delete-refusals");
    let store = engine.indexes();
    let catalog = engine.index_catalog();
    assert_eq!(
        store.delete(&namespace("tenant-a", "missing")),
        Err(IndexOperationError::NamespaceUnknown)
    );
    // An auto index owns its lifecycle to the optimizer (decision D1).
    let registration =
        catalog.register_auto(&tenant("tenant-a"), &auto_definition()).expect("registers");
    assert_eq!(store.delete(registration.namespace()), Err(IndexOperationError::NotExplicitIndex));
    assert_eq!(
        store.stop(registration.namespace()),
        Err(IndexOperationError::NotExplicitIndex),
        "every lifecycle op refuses an auto index"
    );
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

// ---------------------------------------------------------------------------
// The state machine
// ---------------------------------------------------------------------------

/// The operation under test, dispatched through the typed seam.
#[derive(Clone, Copy)]
enum Op {
    Start,
    Stop,
    Enable,
    Disable,
    Reset,
    ClearErrors,
}

impl Op {
    fn apply(
        self,
        store: &IndexStore<'_>,
        ns: &IndexNamespace,
    ) -> Result<super::operations::LifecycleTransition, IndexOperationError> {
        match self {
            Self::Start => store.start(ns),
            Self::Stop => store.stop(ns),
            Self::Enable => store.enable(ns),
            Self::Disable => store.disable(ns),
            Self::Reset => store.reset(ns, IndexResetMode::InPlace),
            Self::ClearErrors => store.clear_errors(ns),
        }
    }

    fn name(self) -> &'static str {
        match self {
            Self::Start => "start",
            Self::Stop => "stop",
            Self::Enable => "enable",
            Self::Disable => "disable",
            Self::Reset => "reset",
            Self::ClearErrors => "clear-errors",
        }
    }

    fn typed(self) -> super::operations::IndexLifecycleOperation {
        match self {
            Self::Start => super::operations::IndexLifecycleOperation::Start,
            Self::Stop => super::operations::IndexLifecycleOperation::Stop,
            Self::Enable => super::operations::IndexLifecycleOperation::Enable,
            Self::Disable => super::operations::IndexLifecycleOperation::Disable,
            Self::Reset => super::operations::IndexLifecycleOperation::Reset,
            Self::ClearErrors => super::operations::IndexLifecycleOperation::ClearErrors,
        }
    }
}

impl IndexOperationalState {
    fn label(self) -> &'static str {
        match self {
            Self::Normal => "normal",
            Self::Paused => "paused",
            Self::Disabled => "disabled",
            Self::Error => "error",
        }
    }
}

/// One expected matrix cell.
enum Expect {
    /// The transition lands; `changed` is true and the state moves.
    To(IndexOperationalState),
    /// The operation succeeds without changing the state; `changed` is
    /// false.
    NoOp(IndexOperationalState),
    /// The transition is refused with `IllegalTransition`.
    Refused,
    /// The transition lands with a real effect while `from == to` (reset's
    /// data effect from Normal).
    ToSelf,
}

#[test]
fn lifecycle_transition_matrix_is_exhaustive_and_typed() {
    use IndexOperationalState::{Disabled, Error, Normal, Paused};
    let (dir, engine) = open_engine("matrix");
    let store = engine.indexes();
    let catalog = engine.index_catalog();

    let expected: &[(IndexOperationalState, Op, Expect)] = &[
        // From Normal.
        (Normal, Op::Start, Expect::NoOp(Normal)),
        (Normal, Op::Stop, Expect::To(Paused)),
        (Normal, Op::Enable, Expect::NoOp(Normal)),
        (Normal, Op::Disable, Expect::To(Disabled)),
        (Normal, Op::Reset, Expect::ToSelf),
        (Normal, Op::ClearErrors, Expect::Refused),
        // From Paused.
        (Paused, Op::Start, Expect::To(Normal)),
        (Paused, Op::Stop, Expect::NoOp(Paused)),
        (Paused, Op::Enable, Expect::NoOp(Paused)),
        (Paused, Op::Disable, Expect::To(Disabled)),
        (Paused, Op::Reset, Expect::To(Normal)),
        (Paused, Op::ClearErrors, Expect::Refused),
        // From Disabled: V1 Index.cs:1040-1041 refuses to start the
        // indexing thread in Disabled and Error.
        (Disabled, Op::Start, Expect::Refused),
        (Disabled, Op::Stop, Expect::Refused),
        (Disabled, Op::Enable, Expect::To(Normal)),
        (Disabled, Op::Disable, Expect::NoOp(Disabled)),
        (Disabled, Op::Reset, Expect::To(Normal)),
        (Disabled, Op::ClearErrors, Expect::Refused),
        // From Error: reset and clear_errors are the documented ways out.
        (Error, Op::Start, Expect::Refused),
        (Error, Op::Stop, Expect::Refused),
        (Error, Op::Enable, Expect::Refused),
        (Error, Op::Disable, Expect::Refused),
        (Error, Op::Reset, Expect::To(Normal)),
        (Error, Op::ClearErrors, Expect::To(Normal)),
    ];
    // The table is the full cartesian product: 4 states x 6 ops.
    assert_eq!(expected.len(), 4 * 6, "the matrix is exhaustive");

    for (from, op, expect) in expected {
        let name = format!("matrix-{}-{}", from.label(), op.name());
        let ns = namespace("tenant-a", &name);
        store.create(&ns, &age_definition(), IndexDeployment::DEFAULT).expect("creates");
        reach(&store, &catalog, &ns, *from);
        assert_eq!(store.state(&ns), Ok(*from), "setup reached {name}");
        let result = op.apply(&store, &ns);
        match expect {
            Expect::To(to) => {
                let transition = result.expect("the legal transition lands");
                assert!(transition.changed(), "{name}: changed is recorded");
                assert_eq!(transition.from(), *from, "{name}: from is reported");
                assert_eq!(transition.to(), *to, "{name}: to is reported");
                assert_eq!(store.state(&ns), Ok(*to), "{name}: the state moved");
            }
            Expect::ToSelf => {
                let transition = result.expect("reset succeeds from the same state");
                assert!(transition.changed(), "{name}: reset changes data, not just state");
                assert_eq!(transition.from(), *from);
                assert_eq!(transition.to(), *from);
                assert_eq!(store.state(&ns), Ok(*from), "{name}: the state stands");
            }
            Expect::NoOp(state) => {
                let transition = result.expect("the idempotent operation succeeds");
                assert!(!transition.changed(), "{name}: no change is recorded");
                assert_eq!(transition.from(), *state);
                assert_eq!(transition.to(), *state);
                assert_eq!(store.state(&ns), Ok(*state), "{name}: the state stands");
            }
            Expect::Refused => {
                assert_eq!(
                    result,
                    Err(IndexOperationError::IllegalTransition {
                        from: *from,
                        operation: op.typed()
                    }),
                    "{name}: the illegal transition is typed"
                );
                assert_eq!(store.state(&ns), Ok(*from), "{name}: refusal leaves the state");
            }
        }
    }
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn refused_transitions_leave_all_index_state_unchanged() {
    let (dir, engine) = open_engine("refusal-no-effects");
    let store = engine.indexes();
    let catalog = engine.index_catalog();
    let ns = create_explicit(&store, "tenant-a", "by-age");
    commit_one(&engine, "tenant-a", "op-1", "doc-1", 42);
    expect_published(store.rebuild(&ns).expect("one build step"));
    store.disable(&ns).expect("disable lands");
    let before = store.statistics(&tenant("tenant-a")).pop().expect("statistics exist");

    let refusals: [(
        Result<super::operations::LifecycleTransition, IndexOperationError>,
        super::operations::IndexLifecycleOperation,
    ); 3] = [
        (store.start(&ns), super::operations::IndexLifecycleOperation::Start),
        (store.stop(&ns), super::operations::IndexLifecycleOperation::Stop),
        (store.clear_errors(&ns), super::operations::IndexLifecycleOperation::ClearErrors),
    ];
    for (refusal, operation) in refusals {
        assert_eq!(
            refusal,
            Err(IndexOperationError::IllegalTransition {
                from: IndexOperationalState::Disabled,
                operation,
            }),
            "the disabled index refuses lifecycle work"
        );
    }
    let after = store.statistics(&tenant("tenant-a")).pop().expect("statistics exist");
    assert_eq!(after.state(), IndexOperationalState::Disabled);
    assert_eq!(after.covered_sequence(), before.covered_sequence(), "coverage stands");
    assert_eq!(after.build_attempts(), before.build_attempts(), "counters stand");
    assert_eq!(after.recorded_errors(), 0, "no errors were fabricated");
    assert!(catalog.segment(&ns).is_some(), "the published segment stands");
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

// ---------------------------------------------------------------------------
// Reset modes and side-by-side swapping
// ---------------------------------------------------------------------------

#[test]
fn in_place_reset_clears_published_data_and_requires_a_rebuild() {
    let (dir, engine) = open_engine("reset-in-place");
    let store = engine.indexes();
    let catalog = engine.index_catalog();
    let ns = create_explicit(&store, "tenant-a", "by-age");
    commit_one(&engine, "tenant-a", "op-1", "doc-1", 42);
    let (epoch, _) = expect_published(store.rebuild(&ns).expect("one build step"));
    assert_eq!(epoch, 1);

    let transition = store.reset(&ns, IndexResetMode::InPlace).expect("in-place reset lands");
    assert!(transition.changed());
    assert!(catalog.segment(&ns).is_none(), "the published data is gone");
    let staleness = store.staleness(&ns).expect("staleness readable");
    assert!(staleness.stale(), "a reset index without a publication is stale");
    assert!(matches!(
        staleness.reasons()[0],
        super::operations::StalenessReason::NotYetPublished { .. }
    ));

    // The rebuild re-publishes from scratch: generation numbering restarted.
    let (epoch, generation) = expect_published(store.rebuild(&ns).expect("rebuild after reset"));
    assert_eq!(epoch, 1, "in-place reset keeps the epoch");
    assert_eq!(generation, 1, "generation numbering restarts");
    assert!(!store.staleness(&ns).expect("staleness readable").stale());
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn side_by_side_reset_keeps_serving_the_old_epoch_until_cutover() {
    let (dir, engine) = open_engine("reset-side-by-side");
    let store = engine.indexes();
    let catalog = engine.index_catalog();
    let ns = create_explicit(&store, "tenant-a", "by-age");
    commit_one(&engine, "tenant-a", "op-1", "doc-1", 42);
    let (epoch, _) = expect_published(store.rebuild(&ns).expect("one build step"));
    assert_eq!(epoch, 1);

    // The side-by-side reset allocates the next epoch and keeps the
    // definition; the old epoch keeps serving.
    let transition =
        store.reset(&ns, IndexResetMode::SideBySide).expect("side-by-side reset lands");
    assert!(transition.changed());
    let definition = catalog
        .definitions(&tenant("tenant-a"))
        .into_iter()
        .find(|entry| entry.namespace().name() == "by-age")
        .expect("the definition survives the reset");
    assert_eq!(definition.definition().fields().len(), 1);
    let serving = catalog.segment(&ns).expect("the old epoch still serves");
    assert_eq!(serving.epoch(), 1, "the old epoch remains the published one");
    assert_eq!(
        catalog.available_generation(&ns).map(ObservedGeneration::epoch),
        Some(1),
        "available generations still resolve to the old epoch"
    );

    // The cutover is the first publication of the new epoch.
    commit_one(&engine, "tenant-a", "op-2", "doc-2", 43);
    let (epoch, generation) = expect_published(store.rebuild(&ns).expect("replacement builds"));
    assert_eq!(epoch, 2, "the replacement publishes into the new epoch");
    assert_eq!(generation, 1, "generations restart inside the epoch");
    let switched = catalog.segment(&ns).expect("a segment is published");
    assert_eq!(switched.epoch(), 2, "the cutover switched the visible segment");
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

// ---------------------------------------------------------------------------
// Staleness per state (GAP-Q9)
// ---------------------------------------------------------------------------

#[test]
fn staleness_is_coverage_based_for_normal_paused_and_disabled_states() {
    let (dir, engine) = open_engine("staleness-coverage");
    let store = engine.indexes();
    let ns = create_explicit(&store, "tenant-a", "by-age");
    assert!(
        !store.staleness(&ns).expect("staleness readable").stale(),
        "an engine with no observed publication has nothing to cover"
    );
    commit_one(&engine, "tenant-a", "op-1", "doc-1", 42);
    assert!(
        store.staleness(&ns).expect("staleness readable").stale(),
        "an unpublished trailing publication is stale"
    );
    expect_published(store.rebuild(&ns).expect("one build step"));
    assert!(
        !store.staleness(&ns).expect("staleness readable").stale(),
        "a caught-up normal index is fresh"
    );

    // Paused: the same coverage rule; the state rides along.
    store.stop(&ns).expect("pause lands");
    assert!(
        !store.staleness(&ns).expect("staleness readable").stale(),
        "a paused index that covers everything is not stale"
    );
    assert_eq!(
        store.staleness(&ns).expect("staleness readable").state(),
        IndexOperationalState::Paused
    );
    commit_one(&engine, "tenant-a", "op-2", "doc-2", 43);
    let stale = store.staleness(&ns).expect("staleness readable");
    assert!(stale.stale(), "a paused index trailing coverage is stale");
    assert!(matches!(
        stale.reasons()[0],
        super::operations::StalenessReason::CoverageBehind { .. }
    ));
    assert!(
        matches!(
            store.rebuild(&ns).expect("rebuild is typed"),
            super::operations::RebuildStepOutcome::Skipped { state: IndexOperationalState::Paused }
        ),
        "a paused index performs no indexing"
    );

    // Disabled: the same coverage rule (V1 DisableIndexOperation.cs:9-10).
    // Resuming from pause is `start`; `enable` addresses the disable axis.
    store.start(&ns).expect("resume lands");
    expect_published(store.rebuild(&ns).expect("one build step"));
    store.disable(&ns).expect("disable lands");
    assert!(
        !store.staleness(&ns).expect("staleness readable").stale(),
        "a disabled index that covers everything is not stale"
    );
    commit_one(&engine, "tenant-a", "op-3", "doc-3", 44);
    assert!(
        store.staleness(&ns).expect("staleness readable").stale(),
        "a disabled index trailing coverage is stale"
    );
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn error_state_is_unconditionally_stale_through_the_query_seam() {
    let (dir, engine) = open_engine("staleness-error");
    let store = engine.indexes();
    let catalog = engine.index_catalog();
    let ns = create_explicit(&store, "tenant-a", "by-age");
    commit_one(&engine, "tenant-a", "op-1", "doc-1", 42);
    expect_published(store.rebuild(&ns).expect("one build step"));
    assert!(!store.staleness(&ns).expect("fresh before the fault").stale());

    // Drive the engine-internal failure ingress to the error threshold.
    for _ in 0..INDEX_ERROR_STATE_THRESHOLD {
        catalog.note_operation_failure(
            &ns,
            IndexErrorAction::Publish,
            IndexErrorCategory::GenerationExhausted,
        );
    }
    let stale = store.staleness(&ns).expect("staleness readable");
    assert!(stale.stale(), "an error index is stale regardless of coverage");
    assert!(matches!(stale.reasons()[0], super::operations::StalenessReason::ErrorState));

    // The query seam observes the same fact through the real catalog.
    let tenant_scope = scope(&engine, "tenant-a");
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let allow = index_query("tenant-a", &ns, StalenessMode::AllowStale);
    let plan = engine.queries().compile(QueryInput::typed(allow)).expect("compiles");
    let result = engine.queries().execute(&plan, &snapshot).expect("allow-stale executes");
    let observation = result.staleness().observation_for(&ns).expect("the namespace is observed");
    assert!(observation.stale(), "the query reports the error index stale");
    assert!(observation.used().is_none(), "an error index vouches for no generation");

    let strict = index_query("tenant-a", &ns, StalenessMode::Strict);
    let plan = engine.queries().compile(QueryInput::typed(strict)).expect("compiles");
    assert_eq!(
        engine.queries().execute(&plan, &snapshot).unwrap_err(),
        QueryError::IndexNotReady,
        "a strict policy refuses an error index instead of trusting it"
    );

    // Clearing the fault restores freshness on the same coverage.
    store.clear_errors(&ns).expect("clearing lands");
    assert!(!store.staleness(&ns).expect("fresh after clearing").stale());
    let strict = index_query("tenant-a", &ns, StalenessMode::Strict);
    let plan = engine.queries().compile(QueryInput::typed(strict)).expect("compiles");
    assert!(engine.queries().execute(&plan, &snapshot).is_ok());
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

// ---------------------------------------------------------------------------
// Bounded wait-for-non-stale (GAP-Q9)
// ---------------------------------------------------------------------------

#[test]
fn wait_for_non_stale_returns_immediately_when_fresh() {
    let (dir, engine) = open_engine("wait-fresh");
    let store = engine.indexes();
    let ns = create_explicit(&store, "tenant-a", "by-age");
    commit_one(&engine, "tenant-a", "op-1", "doc-1", 42);
    expect_published(store.rebuild(&ns).expect("one build step"));
    let outcome = store.wait_for_non_stale(&ns, WaitForNonStale::DEFAULT).expect("wait is typed");
    assert!(outcome.is_non_stale(), "a fresh index returns NonStale without doing work");
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn wait_for_non_stale_drives_bounded_rebuild_steps_to_non_stale() {
    let (dir, engine) = open_engine("wait-progress");
    let store = engine.indexes();
    let ns = create_explicit(&store, "tenant-a", "by-age");
    commit_one(&engine, "tenant-a", "op-1", "doc-1", 42);
    assert!(store.staleness(&ns).expect("trailing").stale());
    let outcome = store
        .wait_for_non_stale(&ns, WaitForNonStale::bounded(Duration::from_secs(30)))
        .expect("wait is typed");
    assert!(outcome.is_non_stale(), "the wait drove the rebuild to freshness");
    let statistics = store.statistics(&tenant("tenant-a")).pop().expect("statistics exist");
    assert!(statistics.build_attempts() >= 1, "the wait's work is observable");
    assert!(statistics.build_successes() >= 1);
    assert!(!statistics.staleness().stale());
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn wait_for_non_stale_returns_deterministically_when_the_bound_hits() {
    let (dir, engine) = open_engine("wait-zero-bound");
    let store = engine.indexes();
    let ns = create_explicit(&store, "tenant-a", "by-age");
    commit_one(&engine, "tenant-a", "op-1", "doc-1", 42);
    let outcome = store
        .wait_for_non_stale(&ns, WaitForNonStale::bounded(Duration::ZERO))
        .expect("a zero bound is legal and evaluates once");
    assert!(!outcome.is_non_stale(), "the namespace was stale");
    assert!(outcome.staleness().stale(), "the timed-out outcome carries the staleness it observed");
    let statistics = store.statistics(&tenant("tenant-a")).pop().expect("statistics exist");
    assert_eq!(statistics.build_attempts(), 0, "a zero bound must not run a single rebuild step");
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn wait_for_non_stale_terminates_without_sleeping_when_no_progress_is_possible() {
    let (dir, engine) = open_engine("wait-no-progress");
    let store = engine.indexes();
    let ns = create_explicit(&store, "tenant-a", "by-age");
    commit_one(&engine, "tenant-a", "op-1", "doc-1", 42);
    expect_published(store.rebuild(&ns).expect("one build step"));
    commit_one(&engine, "tenant-a", "op-2", "doc-2", 43);
    store.stop(&ns).expect("pause lands");
    // The default bound is 15 seconds; a paused index can make no progress,
    // so the wait must end on the no-progress rule, not by sleeping.
    let outcome = store.wait_for_non_stale(&ns, WaitForNonStale::DEFAULT).expect("wait is typed");
    assert!(!outcome.is_non_stale());
    let statistics = store.statistics(&tenant("tenant-a")).pop().expect("statistics exist");
    assert_eq!(
        statistics.build_attempts(),
        1,
        "the wait performed no additional work for a paused index"
    );
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

// ---------------------------------------------------------------------------
// Recorded errors (PB-4055)
// ---------------------------------------------------------------------------

#[test]
fn build_failures_are_recorded_content_safe_and_bounded() {
    let (dir, engine) = open_engine("errors-bounded");
    let store = engine.indexes();
    let catalog = engine.index_catalog();
    let ns = create_explicit(&store, "tenant-a", "by-age");
    for _ in 0..u64::try_from(MAX_INDEX_ERRORS_PER_INDEX + 16).expect("fits") {
        catalog.note_operation_failure(
            &ns,
            IndexErrorAction::Build,
            IndexErrorCategory::StorageFailure,
        );
    }
    let listings = store.errors(&tenant("tenant-a"));
    assert_eq!(listings.len(), 1, "one index with recorded errors");
    assert_eq!(listings[0].name(), "by-age");
    assert_eq!(
        listings[0].records().len(),
        MAX_INDEX_ERRORS_PER_INDEX,
        "the error list is bounded to the newest records"
    );
    for record in listings[0].records() {
        assert_eq!(record.action(), IndexErrorAction::Build);
        assert_eq!(record.category(), IndexErrorCategory::StorageFailure);
    }
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn repeated_failures_enter_error_state_and_success_resets_the_streak() {
    let (dir, engine) = open_engine("errors-threshold");
    let store = engine.indexes();
    let catalog = engine.index_catalog();
    let ns = create_explicit(&store, "tenant-a", "by-age");

    // Below the threshold the index stays Normal.
    for _ in 0..INDEX_ERROR_STATE_THRESHOLD - 1 {
        catalog.note_operation_failure(
            &ns,
            IndexErrorAction::Build,
            IndexErrorCategory::StorageFailure,
        );
    }
    assert_eq!(store.state(&ns), Ok(IndexOperationalState::Normal));

    // A successful step resets the consecutive-failure streak.
    commit_one(&engine, "tenant-a", "op-1", "doc-1", 42);
    expect_published(store.rebuild(&ns).expect("one build step"));
    for _ in 0..INDEX_ERROR_STATE_THRESHOLD - 1 {
        catalog.note_operation_failure(
            &ns,
            IndexErrorAction::Build,
            IndexErrorCategory::StorageFailure,
        );
    }
    assert_eq!(
        store.state(&ns),
        Ok(IndexOperationalState::Normal),
        "the streak restarted from the success"
    );

    // Crossing the threshold enters Error; clearing returns to Normal.
    catalog.note_operation_failure(
        &ns,
        IndexErrorAction::Build,
        IndexErrorCategory::StorageFailure,
    );
    assert_eq!(store.state(&ns), Ok(IndexOperationalState::Error));
    let transition = store.clear_errors(&ns).expect("clearing lands");
    assert!(transition.changed());
    assert_eq!(store.state(&ns), Ok(IndexOperationalState::Normal));
    assert!(store.errors(&tenant("tenant-a")).is_empty(), "clearing empties the recorded list");
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn failures_do_not_record_for_indexes_that_are_not_indexing() {
    let (dir, engine) = open_engine("errors-not-indexing");
    let store = engine.indexes();
    let catalog = engine.index_catalog();
    let ns = create_explicit(&store, "tenant-a", "by-age");
    store.stop(&ns).expect("pause lands");
    catalog.note_operation_failure(
        &ns,
        IndexErrorAction::Build,
        IndexErrorCategory::StorageFailure,
    );
    assert!(
        store.errors(&tenant("tenant-a")).is_empty(),
        "a paused index indexes nothing, so nothing can fail"
    );
    store.disable(&ns).expect("disable lands");
    catalog.note_operation_failure(
        &ns,
        IndexErrorAction::Build,
        IndexErrorCategory::StorageFailure,
    );
    assert!(store.errors(&tenant("tenant-a")).is_empty());
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

// ---------------------------------------------------------------------------
// Statistics (PB-4056 subset, GAP-D18 residual)
// ---------------------------------------------------------------------------

#[test]
fn index_statistics_report_the_engine_owned_v1_subset() {
    let (dir, engine) = open_engine("stats-index");
    let store = engine.indexes();
    let ns = namespace("tenant-a", "by-age");
    store
        .create(
            &ns,
            &age_definition(),
            IndexDeployment::new(IndexPriority::Low, IndexLockMode::LockedIgnore),
        )
        .expect("creates");
    commit_one(&engine, "tenant-a", "op-1", "doc-1", 42);
    expect_published(store.rebuild(&ns).expect("one build step"));
    let statistics: Vec<IndexStatistics> = store.statistics(&tenant("tenant-a"));
    assert_eq!(statistics.len(), 1);
    let view = &statistics[0];
    assert_eq!(view.tenant(), &tenant("tenant-a"));
    assert_eq!(view.name(), "by-age");
    assert_eq!(view.state(), IndexOperationalState::Normal);
    assert_eq!(view.priority(), IndexPriority::Low);
    assert_eq!(view.lock_mode(), IndexLockMode::LockedIgnore);
    assert!(!view.staleness().stale());
    assert!(view.covered_sequence().is_some());
    assert_eq!(
        view.published().map(|generation| (generation.epoch(), generation.generation())),
        Some((1, 1))
    );
    assert_eq!(view.entries(), 1, "one indexed document");
    assert_eq!(view.build_attempts(), 1);
    assert_eq!(view.build_successes(), 1);
    assert_eq!(view.build_failures(), 0);
    assert_eq!(view.recorded_errors(), 0);
    assert!(view.last_indexed().is_some(), "the last successful publication is recorded");
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn engine_statistics_aggregate_per_tenant_and_state() {
    let (dir, engine) = open_engine("stats-engine");
    let store = engine.indexes();
    let catalog = engine.index_catalog();
    let normal = create_explicit(&store, "tenant-a", "by-age");
    let paused = create_explicit(&store, "tenant-a", "by-pause");
    store.stop(&paused).expect("pause lands");
    let disabled = create_explicit(&store, "tenant-b", "by-disable");
    store.disable(&disabled).expect("disable lands");
    let faulty = create_explicit(&store, "tenant-b", "by-error");
    for _ in 0..INDEX_ERROR_STATE_THRESHOLD {
        catalog.note_operation_failure(
            &faulty,
            IndexErrorAction::Build,
            IndexErrorCategory::StorageFailure,
        );
    }
    commit_one(&engine, "tenant-a", "op-1", "doc-1", 42);
    commit_one(&engine, "tenant-b", "op-2", "doc-2", 43);
    expect_published(store.rebuild(&normal).expect("one build step"));
    assert!(
        matches!(
            store.rebuild(&disabled).expect("the step is typed"),
            super::operations::RebuildStepOutcome::Skipped { .. }
        ),
        "a disabled index performs no indexing"
    );

    let aggregate = store.engine_statistics();
    assert_eq!(aggregate.len(), 2, "one row per tenant");
    assert_eq!(aggregate[0].tenant(), &tenant("tenant-a"), "tenants in name order");
    assert_eq!(aggregate[0].count_in(IndexOperationalState::Normal), 1);
    assert_eq!(aggregate[0].count_in(IndexOperationalState::Paused), 1);
    assert_eq!(aggregate[0].total(), 2);
    assert_eq!(aggregate[0].build_attempts(), 1);
    assert_eq!(aggregate[0].build_successes(), 1);
    assert_eq!(aggregate[0].recorded_errors(), 0);
    assert_eq!(aggregate[1].tenant(), &tenant("tenant-b"));
    assert_eq!(aggregate[1].count_in(IndexOperationalState::Disabled), 1);
    assert_eq!(aggregate[1].count_in(IndexOperationalState::Error), 1);
    assert_eq!(
        aggregate[1].recorded_errors(),
        INDEX_ERROR_STATE_THRESHOLD,
        "recorded errors are totalled"
    );
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}
