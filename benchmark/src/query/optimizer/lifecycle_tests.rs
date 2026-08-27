//! In-crate lifecycle tests covering the IX-03 contract list.
//!
//! Every test crosses the public seam the way the IX-02 suite does: plans
//! compile through [`QueryStore`](super::super::QueryStore), execute against
//! real snapshots of a durable Engine, and lifecycle truth is observed through
//! the typed reads on the store. Catalog setup that has no public operation
//! yet (registration, rebuilds, supersede scheduling, the lifecycle clock and
//! thresholds) drives the Engine-owned catalog through its crate-private
//! surface. A pure collection-scan query drives one maintenance pass without
//! selecting or creating anything, which is how transitions are observed
//! without waking the index they fired on. Nothing here claims a V1 parity
//! row, a speed result, or an index-accelerated execution path.

use std::{collections::BTreeMap, path::PathBuf, time::Duration};

use super::{
    AUTO_NAMESPACE_PREFIX, CandidateNote, CandidateVerdict, CreationRefusal, IndexCapabilities,
    IndexDefinition, IndexField, IndexKind, IndexSelectionRecord, MAX_AUTO_INDEXES_PER_TENANT,
    SelectionOutcome, TieBreak,
    lifecycle::{IndexLifecycleState, LifecycleThresholds},
};
use crate::query::{
    IndexNamespace, OrderDirection, OrderingKey, QueryCondition, QueryError, QueryOrdering,
    QueryPaging, QueryPredicate, QueryProjection, QueryResult, QuerySource, QuerySpec,
    StalenessMode, StalenessPolicy,
};
use crate::{
    engine::{CommitOutcome, Engine, EngineConfig},
    index::{IndexBuilder, RebuildOutcome},
    operations::QueryInput,
    types::{
        Cancellation, CollectionName, Deadline, Document, DocumentId, DocumentValue,
        ExpectedVersion, LocalDurability, OperationId, TenantId, TenantScope,
    },
};

fn location(tag: &str) -> PathBuf {
    std::env::temp_dir().join(format!("tbv2-ix03-lifecycle-{tag}-{}", std::process::id()))
}

fn open_engine(tag: &str) -> (PathBuf, Engine) {
    let dir = location(tag);
    std::fs::remove_dir_all(&dir).ok();
    let engine =
        Engine::open(EngineConfig::durable(&dir).expect("durable location")).expect("engine opens");
    (dir, engine)
}

fn reopen_engine(dir: &PathBuf) -> Engine {
    Engine::open(EngineConfig::durable(dir).expect("durable location")).expect("engine reopens")
}

fn tenant(name: &str) -> TenantId {
    TenantId::try_new(name).expect("valid tenant")
}

fn scope(engine: &Engine, name: &str) -> TenantScope {
    TenantScope::from_parts(tenant(name), engine.identity())
}

fn object_document(id: &str, collection: &str, fields: &[(&str, DocumentValue)]) -> Document {
    Document::new(
        DocumentId::try_new(id).expect("valid identifier"),
        CollectionName::try_new(collection).expect("valid identifier"),
        DocumentValue::Object(
            fields
                .iter()
                .map(|(name, value)| ((*name).to_owned(), value.clone()))
                .collect::<BTreeMap<_, _>>(),
        ),
    )
}

fn commit_documents(
    engine: &Engine,
    tenant_scope: &TenantScope,
    tag: &str,
    documents: Vec<Document>,
) -> u64 {
    let snapshot = engine.read_snapshot(tenant_scope).expect("snapshot");
    let operation = OperationId::try_new(tag).expect("valid identifier");
    let mut transaction = engine.begin(tenant_scope, snapshot, operation).expect("begins");
    for document in documents {
        transaction.put(document, ExpectedVersion::Any).expect("stages");
    }
    match transaction.commit(LocalDurability::Synced) {
        CommitOutcome::Committed(receipt) => receipt.sequence().get(),
        other => panic!("commit did not succeed: {other:?}"),
    }
}

/// Commits users with an integer age and a string name, the two fields the
/// widening rules play with.
fn commit_users(engine: &Engine, tenant_name: &str, tag: &str, ages: &[i64]) -> u64 {
    let documents = ages
        .iter()
        .enumerate()
        .map(|(index, age)| {
            object_document(
                &format!("doc-{tag}-{index}"),
                "users",
                &[
                    ("age", DocumentValue::Integer(*age)),
                    ("name", DocumentValue::String(format!("n{index}"))),
                ],
            )
        })
        .collect();
    commit_documents(engine, &scope(engine, tenant_name), tag, documents)
}

fn source(collection: &str) -> QuerySource {
    QuerySource::try_new(CollectionName::try_new(collection).expect("valid identifier"))
        .expect("user collection")
}

fn equals(field: &str, value: i64) -> QueryCondition {
    QueryCondition::Equals { field: field.to_owned(), value: DocumentValue::Integer(value) }
}

fn above(field: &str, value: i64) -> QueryCondition {
    QueryCondition::GreaterThan { field: field.to_owned(), value: DocumentValue::Integer(value) }
}

fn predicate(conditions: Vec<QueryCondition>) -> QueryPredicate {
    QueryPredicate::all(conditions).expect("valid conjunction")
}

fn ordering(keys: Vec<(&str, OrderDirection)>) -> QueryOrdering {
    QueryOrdering::try_new(
        keys.into_iter()
            .map(|(field, direction)| OrderingKey::try_new(field, direction).expect("valid key"))
            .collect(),
    )
    .expect("valid ordering")
}

/// A dynamic specification: the strict policy names no index, which is the
/// optimizer's route at this seam.
fn dynamic_spec(
    claim: &str,
    collection: &str,
    predicate: QueryPredicate,
    ordering_keys: Vec<(&str, OrderDirection)>,
) -> QuerySpec {
    QuerySpec::try_new(
        tenant(claim),
        source(collection),
        predicate,
        ordering(ordering_keys),
        QueryProjection::whole_document(),
        QueryPaging::try_new(0, None).expect("valid paging"),
        StalenessPolicy::try_new(StalenessMode::Strict, Vec::new()).expect("valid policy"),
        Deadline::after(Duration::from_secs(30)).expect("valid deadline"),
        Cancellation::new(),
    )
    .expect("valid specification")
}

fn users_spec(claim: &str, predicate: QueryPredicate) -> QuerySpec {
    dynamic_spec(claim, "users", predicate, Vec::new())
}

/// Executes one dynamic query and returns its result; every dynamic execution
/// runs one optimizer pass, so this is also what drives the maintenance pass.
fn execute_dynamic(
    engine: &Engine,
    spec: QuerySpec,
    tenant_name: &str,
) -> Result<QueryResult, QueryError> {
    let store = engine.queries();
    let plan = store.compile(QueryInput::typed(spec)).expect("compiles");
    let snapshot = engine.read_snapshot(&scope(engine, tenant_name)).expect("snapshot");
    store.execute(&plan, &snapshot)
}

/// Drives exactly one maintenance pass through the public seam without
/// selecting or creating anything: the pure collection scan (AX-1) runs the
/// optimizer pass head only, so transitions fire without waking their
/// subject.
fn run_maintenance(engine: &Engine, tenant_name: &str) {
    execute_dynamic(
        engine,
        dynamic_spec(tenant_name, "users", predicate(Vec::new()), Vec::new()),
        tenant_name,
    )
    .expect("the collection scan executes");
}

fn records(engine: &Engine, tenant_name: &str) -> Vec<IndexSelectionRecord> {
    engine.queries().recent_index_selections(&tenant(tenant_name))
}

fn last_record(engine: &Engine, tenant_name: &str) -> IndexSelectionRecord {
    let mut all = records(engine, tenant_name);
    all.pop().expect("at least one record")
}

fn definitions(engine: &Engine, tenant_name: &str) -> Vec<super::RegisteredIndex> {
    engine.queries().registered_indexes(&tenant(tenant_name))
}

fn auto_field(field: &str) -> IndexField {
    IndexField::try_new(field, IndexCapabilities::FULL).expect("valid field")
}

fn auto_definition(collection: &str, fields: &[&str]) -> IndexDefinition {
    IndexDefinition::try_new(
        CollectionName::try_new(collection).expect("valid identifier"),
        fields.iter().map(|field| auto_field(field)).collect(),
        IndexKind::Auto,
    )
    .expect("valid definition")
}

fn explicit_definition(
    collection: &str,
    field: &str,
    capabilities: IndexCapabilities,
) -> IndexDefinition {
    IndexDefinition::try_new(
        CollectionName::try_new(collection).expect("valid identifier"),
        vec![IndexField::try_new(field, capabilities).expect("valid field")],
        IndexKind::Explicit,
    )
    .expect("valid definition")
}

fn auto_namespace(tenant_name: &str, name: &str) -> IndexNamespace {
    IndexNamespace::try_new(tenant(tenant_name), name).expect("valid namespace")
}

/// Registers an auto definition directly, without a query marking it queried:
/// the crate-private setup path. Returns its namespace.
fn register_auto_direct(
    engine: &Engine,
    tenant_name: &str,
    definition: &IndexDefinition,
) -> IndexNamespace {
    engine
        .index_catalog()
        .register_auto(&tenant(tenant_name), definition)
        .expect("registers")
        .namespace()
        .clone()
}

/// Builds one namespace from the latest observed publication and returns the
/// covered sequence it published at.
fn build_latest(engine: &Engine, namespace: &IndexNamespace) -> u64 {
    let catalog = engine.index_catalog();
    let delta = catalog.delta().expect("publication observed");
    match IndexBuilder::new(&catalog, engine).rebuild_from(namespace, &delta, engine.identity()) {
        Ok(RebuildOutcome::Published { covered_sequence, .. }) => covered_sequence,
        other => panic!("expected a publication, got {other:?}"),
    }
}

/// Sets the full threshold set: every gate explicit and deterministic.
fn thresholds(engine: &Engine, idle_after: Duration, delete_after: Duration, drain: Duration) {
    engine.index_catalog().set_lifecycle_thresholds(LifecycleThresholds::new(
        idle_after,
        delete_after,
        drain,
    ));
}

/// Advances the catalog's lifecycle clock, the deterministic time source the
/// state machine measures against.
fn advance_clock(engine: &Engine, by: Duration) {
    engine.index_catalog().advance_lifecycle_clock(by);
}

fn find_index(engine: &Engine, tenant_name: &str, name: &str) -> super::RegisteredIndex {
    definitions(engine, tenant_name)
        .into_iter()
        .find(|index| index.name() == name)
        .unwrap_or_else(|| panic!("index {name} is registered"))
}

/// The live registry: retired tombstones stay listed in the typed view, so
/// the live-state assertions filter them out and check them by name.
fn states_of(engine: &Engine, tenant_name: &str) -> Vec<(String, IndexLifecycleState)> {
    definitions(engine, tenant_name)
        .iter()
        .filter(|index| index.state() != IndexLifecycleState::Retired)
        .map(|index| (index.name().to_owned(), index.state()))
        .collect()
}

fn chosen_name(record: &IndexSelectionRecord) -> String {
    match record.outcome() {
        SelectionOutcome::ChosenIndex { name, .. }
        | SelectionOutcome::CreatedAutoIndex { name, .. } => name.clone(),
        other => panic!("expected a serving index, got {other:?}"),
    }
}

// ---------------------------------------------------------------------------
// The IX-03 red-test list
// ---------------------------------------------------------------------------

#[test]
fn partial_match_widens_the_mapping_and_supersedes_after_the_drain_wait() {
    let (dir, engine) = open_engine("widening-supersede");
    commit_users(&engine, "tenant-a", "op-1", &[5, 3]);

    // The first query creates the narrow index; the second demands more and
    // only partially matches it, so the mapping widens to the union and the
    // narrow index is superseded by the covering one (AX-16).
    execute_dynamic(&engine, users_spec("tenant-a", predicate(vec![equals("age", 5)])), "tenant-a")
        .expect("executes");
    let record = last_record(&engine, "tenant-a");
    assert_eq!(chosen_name(&record), "Auto/users/ByAge");

    execute_dynamic(
        &engine,
        users_spec("tenant-a", predicate(vec![equals("age", 5), equals("name", 3)])),
        "tenant-a",
    )
    .expect("executes");
    let record = last_record(&engine, "tenant-a");
    assert_eq!(
        chosen_name(&record),
        "Auto/users/ByAgeAndName",
        "the partial match widened into the covering union identity"
    );

    // The superseded index survives until the replacement covers it and the
    // drain wait has elapsed (AX-17): with a 30-second drain it is still
    // there after the next pass.
    thresholds(&engine, Duration::from_hours(1), Duration::from_hours(2), Duration::from_secs(30));
    execute_dynamic(&engine, users_spec("tenant-a", predicate(vec![equals("age", 5)])), "tenant-a")
        .expect("executes");
    assert_eq!(
        states_of(&engine, "tenant-a"),
        vec![
            ("Auto/users/ByAge".to_owned(), IndexLifecycleState::Active),
            ("Auto/users/ByAgeAndName".to_owned(), IndexLifecycleState::Active),
        ],
        "the superseded index drains before deletion"
    );

    // Once the drain elapses the target retires with the measured value and
    // threshold recorded (AX-17/AX-21 observability).
    advance_clock(&engine, Duration::from_secs(31));
    execute_dynamic(&engine, users_spec("tenant-a", predicate(vec![equals("age", 5)])), "tenant-a")
        .expect("executes");
    let retired = find_index(&engine, "tenant-a", "Auto/users/ByAge");
    assert_eq!(retired.state(), IndexLifecycleState::Retired);
    assert_eq!(retired.kind(), IndexKind::Auto);
    let transition = retired.transitions().last().expect("the retirement is recorded");
    assert_eq!(transition.subject(), "Auto/users/ByAge");
    assert_eq!(transition.from(), IndexLifecycleState::Active);
    assert_eq!(transition.to(), IndexLifecycleState::Retired);
    assert_eq!(transition.threshold(), Duration::from_secs(30), "the drain threshold is recorded");
    assert!(
        transition.measured() >= transition.threshold(),
        "the measured drain value is recorded against its threshold"
    );
    assert_eq!(
        find_index(&engine, "tenant-a", "Auto/users/ByAgeAndName").state(),
        IndexLifecycleState::Active,
        "the replacement stays live"
    );
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn supersede_deletion_waits_for_replacement_coverage_before_the_drain() {
    let (dir, engine) = open_engine("coverage-gate");
    commit_users(&engine, "tenant-a", "op-1", &[5, 3]);
    let catalog = engine.index_catalog();

    // The narrow index is ahead: it published at the newest coverage while
    // the wider replacement published at an older one.
    let narrow = register_auto_direct(&engine, "tenant-a", &auto_definition("users", &["age"]));
    let wide =
        register_auto_direct(&engine, "tenant-a", &auto_definition("users", &["age", "name"]));
    let delta_first = catalog.delta().expect("publication");
    commit_users(&engine, "tenant-a", "op-2", &[7]);
    let delta_second = catalog.delta().expect("publication");
    let builder = IndexBuilder::new(&catalog, &engine);
    builder
        .rebuild_from(&narrow, &delta_second, engine.identity())
        .expect("narrow publishes at the newest coverage");
    builder
        .rebuild_from(&wide, &delta_first, engine.identity())
        .expect("wide publishes at the older coverage");

    catalog.mark_queried(&narrow);
    catalog.mark_queried(&wide);
    catalog.schedule_supersede(&wide, &narrow);
    thresholds(&engine, Duration::from_hours(1), Duration::from_hours(2), Duration::from_secs(10));

    // Drain elapsed since the scheduling, but coverage has not reached the
    // target: the deletion must not fire. The pass drives the replacement's
    // catch-up build itself, so coverage parity arrives during this query
    // and the drain clock starts there.
    execute_dynamic(&engine, users_spec("tenant-a", predicate(vec![equals("age", 5)])), "tenant-a")
        .expect("executes");
    assert_eq!(
        find_index(&engine, "tenant-a", "Auto/users/ByAge").state(),
        IndexLifecycleState::Active,
        "coverage parity was only just reached; the drain clock starts now"
    );

    // Once the drain since parity elapses, the target retires.
    advance_clock(&engine, Duration::from_secs(11));
    execute_dynamic(&engine, users_spec("tenant-a", predicate(vec![equals("age", 5)])), "tenant-a")
        .expect("executes");
    assert_eq!(
        find_index(&engine, "tenant-a", "Auto/users/ByAge").state(),
        IndexLifecycleState::Retired
    );
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn surpass_pass_deletes_an_auto_index_another_completely_serves() {
    let (dir, engine) = open_engine("surpass-complete");
    commit_users(&engine, "tenant-a", "op-1", &[5, 3]);

    // Two auto indexes exist with no query path having superseded them: the
    // background surpass pass must retire the narrower one (AX-18).
    let narrow = register_auto_direct(&engine, "tenant-a", &auto_definition("users", &["age"]));
    let wide =
        register_auto_direct(&engine, "tenant-a", &auto_definition("users", &["age", "name"]));
    build_latest(&engine, &narrow);
    build_latest(&engine, &wide);
    engine.index_catalog().mark_queried(&narrow);
    engine.index_catalog().mark_queried(&wide);
    thresholds(&engine, Duration::from_hours(1), Duration::from_hours(2), Duration::ZERO);

    execute_dynamic(&engine, users_spec("tenant-a", predicate(vec![equals("age", 5)])), "tenant-a")
        .expect("executes");
    assert_eq!(
        states_of(&engine, "tenant-a"),
        vec![("Auto/users/ByAgeAndName".to_owned(), IndexLifecycleState::Active)],
        "the completely served narrow index is retired by the surpass pass"
    );
    let retired = find_index(&engine, "tenant-a", "Auto/users/ByAge");
    assert_eq!(retired.state(), IndexLifecycleState::Retired);
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn surpass_pass_extends_partially_served_indexes_one_extension_per_pass() {
    let (dir, engine) = open_engine("surpass-partial");
    commit_users(&engine, "tenant-a", "op-1", &[5, 3]);

    // Two disjoint auto indexes: neither serves the other, so the pass
    // widens one subject into the union (AX-18), one extension per pass.
    let by_age = register_auto_direct(&engine, "tenant-a", &auto_definition("users", &["age"]));
    let by_name = register_auto_direct(&engine, "tenant-a", &auto_definition("users", &["name"]));
    build_latest(&engine, &by_age);
    build_latest(&engine, &by_name);
    engine.index_catalog().mark_queried(&by_age);
    engine.index_catalog().mark_queried(&by_name);
    thresholds(&engine, Duration::from_hours(1), Duration::from_hours(2), Duration::ZERO);

    // Pass one: the extension creates the union and supersedes its subject.
    execute_dynamic(&engine, users_spec("tenant-a", predicate(vec![equals("age", 5)])), "tenant-a")
        .expect("executes");
    assert_eq!(
        states_of(&engine, "tenant-a"),
        vec![
            ("Auto/users/ByAgeAndName".to_owned(), IndexLifecycleState::Active),
            ("Auto/users/ByName".to_owned(), IndexLifecycleState::Active),
        ],
        "one extension per pass: only the subject was replaced"
    );

    // Pass two: the union now completely serves the remaining index, whose
    // deletion follows the (zero) drain in the same pass.
    execute_dynamic(&engine, users_spec("tenant-a", predicate(vec![equals("age", 5)])), "tenant-a")
        .expect("executes");
    assert_eq!(
        states_of(&engine, "tenant-a"),
        vec![("Auto/users/ByAgeAndName".to_owned(), IndexLifecycleState::Active)],
        "the union converges as the sole auto index of the collection"
    );
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn idle_demotes_but_stays_selectable_and_a_query_wakes_it() {
    let (dir, engine) = open_engine("idle-wake");
    commit_users(&engine, "tenant-a", "op-1", &[5, 3]);

    // One serving index, marked queried by the query that created it.
    execute_dynamic(&engine, users_spec("tenant-a", predicate(vec![equals("age", 5)])), "tenant-a")
        .expect("executes");
    thresholds(&engine, Duration::from_mins(1), Duration::from_hours(1), Duration::ZERO);

    // Unqueried past the idle threshold: the next maintenance pass demotes
    // it to idle with the measured value and threshold recorded (AX-21). A
    // collection scan drives the pass without selecting the index, so it
    // stays demoted for observation.
    advance_clock(&engine, Duration::from_secs(61));
    run_maintenance(&engine, "tenant-a");
    let idle = find_index(&engine, "tenant-a", "Auto/users/ByAge");
    assert_eq!(idle.state(), IndexLifecycleState::Idle);
    let transition = idle.transitions().last().expect("the demotion is recorded");
    assert_eq!(transition.subject(), "Auto/users/ByAge");
    assert_eq!(transition.from(), IndexLifecycleState::Active);
    assert_eq!(transition.to(), IndexLifecycleState::Idle);
    assert_eq!(transition.threshold(), Duration::from_mins(1));
    assert!(transition.measured() >= transition.threshold());

    // An idle index stays selectable: the next query still chooses it —
    // here from the cached shape, which wakes it exactly like a full match
    // would — and wakes it (AX-22). The complete-but-idle verdict of a full
    // match is asserted in the ranking test below.
    execute_dynamic(&engine, users_spec("tenant-a", predicate(vec![equals("age", 5)])), "tenant-a")
        .expect("executes");
    let record = last_record(&engine, "tenant-a");
    assert_eq!(chosen_name(&record), "Auto/users/ByAge");
    assert!(record.cache_hit(), "a cached selection of an idle index still selects and wakes it");
    let woken = find_index(&engine, "tenant-a", "Auto/users/ByAge");
    assert_eq!(woken.state(), IndexLifecycleState::Active, "querying wakes the idle index");
    let wake = woken.transitions().last().expect("the wake is recorded");
    assert_eq!(wake.from(), IndexLifecycleState::Idle);
    assert_eq!(wake.to(), IndexLifecycleState::Active);
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn idle_ranking_demotes_an_idle_candidate_only_at_equal_recency() {
    let (dir, engine) = open_engine("idle-ranking");
    commit_users(&engine, "tenant-a", "op-1", &[5, 3]);
    let catalog = engine.index_catalog();

    // Two complete candidates at equal coverage: the narrow one idle, the
    // wide one active. V1's rule 3 demotes the idle one (AX-13).
    let narrow = register_auto_direct(&engine, "tenant-a", &auto_definition("users", &["age"]));
    let wide =
        register_auto_direct(&engine, "tenant-a", &auto_definition("users", &["age", "name"]));
    build_latest(&engine, &narrow);
    build_latest(&engine, &wide);
    catalog.mark_queried(&narrow);
    catalog.mark_queried(&wide);
    thresholds(&engine, Duration::from_mins(1), Duration::from_hours(1), Duration::from_hours(1));

    // Age the narrow index past the idle threshold while the wide one stays
    // fresh, then run the pass that demotes it.
    advance_clock(&engine, Duration::from_secs(61));
    catalog.mark_queried(&wide);
    run_maintenance(&engine, "tenant-a");
    assert_eq!(
        find_index(&engine, "tenant-a", "Auto/users/ByAge").state(),
        IndexLifecycleState::Idle
    );

    execute_dynamic(&engine, users_spec("tenant-a", predicate(vec![equals("age", 5)])), "tenant-a")
        .expect("executes");
    let record = last_record(&engine, "tenant-a");
    assert_eq!(chosen_name(&record), "Auto/users/ByAgeAndName", "the active candidate wins");
    assert!(record.tie_breaks().contains(&TieBreak::NonIdlePreferred));
    let idle_candidate = record
        .candidates()
        .iter()
        .find(|candidate| candidate.name() == "Auto/users/ByAge")
        .expect("the idle candidate is considered");
    assert_eq!(idle_candidate.verdict(), &CandidateVerdict::CompleteButIdle);
    assert_eq!(idle_candidate.note(), Some(CandidateNote::IdleDemoted));
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn idle_past_the_deletion_threshold_retires_with_measured_value_and_threshold() {
    let (dir, engine) = open_engine("idle-delete");
    commit_users(&engine, "tenant-a", "op-1", &[5, 3]);

    execute_dynamic(&engine, users_spec("tenant-a", predicate(vec![equals("age", 5)])), "tenant-a")
        .expect("executes");
    thresholds(&engine, Duration::from_mins(1), Duration::from_mins(10), Duration::ZERO);

    advance_clock(&engine, Duration::from_secs(61));
    run_maintenance(&engine, "tenant-a");
    assert_eq!(
        find_index(&engine, "tenant-a", "Auto/users/ByAge").state(),
        IndexLifecycleState::Idle,
        "idle first, deletion only after the idle period elapses"
    );

    advance_clock(&engine, Duration::from_secs(601));
    run_maintenance(&engine, "tenant-a");
    let retired = find_index(&engine, "tenant-a", "Auto/users/ByAge");
    assert_eq!(retired.state(), IndexLifecycleState::Retired);
    let transition = retired.transitions().last().expect("recorded");
    assert_eq!(transition.from(), IndexLifecycleState::Idle);
    assert_eq!(transition.to(), IndexLifecycleState::Retired);
    assert_eq!(transition.threshold(), Duration::from_mins(10));
    assert!(transition.measured() >= transition.threshold());
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn no_transition_issues_while_the_reporting_state_is_incomplete() {
    let (dir, engine) = open_engine("reporting-gate");
    commit_users(&engine, "tenant-a", "op-1", &[5, 3]);

    // A registered auto index no query has ever marked queried: its usage
    // report is missing, so the fail-closed observer rule forbids every
    // transition (AX-21), no matter how far the clock moves.
    let registered = register_auto_direct(&engine, "tenant-a", &auto_definition("users", &["age"]));
    build_latest(&engine, &registered);
    thresholds(&engine, Duration::ZERO, Duration::ZERO, Duration::ZERO);
    advance_clock(&engine, Duration::from_secs(10_000_000));
    run_maintenance(&engine, "tenant-a");

    let unreported = find_index(&engine, "tenant-a", "Auto/users/ByAge");
    assert_eq!(unreported.state(), IndexLifecycleState::Active);
    assert!(
        unreported.transitions().is_empty(),
        "no idle demotion and no deletion without a usage report"
    );
    assert!(!unreported.queried(), "the reporting state stays observable");

    // The first query that selects it completes the report; from there the
    // state machine may act.
    execute_dynamic(&engine, users_spec("tenant-a", predicate(vec![equals("age", 5)])), "tenant-a")
        .expect("executes");
    assert!(find_index(&engine, "tenant-a", "Auto/users/ByAge").queried());
    advance_clock(&engine, Duration::from_secs(1));
    run_maintenance(&engine, "tenant-a");
    assert_eq!(
        find_index(&engine, "tenant-a", "Auto/users/ByAge").state(),
        IndexLifecycleState::Idle,
        "the completed report unlocks the transition"
    );
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn explicit_indexes_are_never_auto_retired_superseded_or_widened_into() {
    let (dir, engine) = open_engine("explicit-immune");
    commit_users(&engine, "tenant-a", "op-1", &[5, 3]);
    let catalog = engine.index_catalog();

    // An explicit index a wider auto index completely serves: the surpass
    // pass must leave it alone (never auto-retiring an explicit index).
    let explicit = auto_namespace("tenant-a", "users/by-age");
    catalog
        .register_explicit(&explicit, &explicit_definition("users", "age", IndexCapabilities::FULL))
        .expect("registers");
    let wide =
        register_auto_direct(&engine, "tenant-a", &auto_definition("users", &["age", "name"]));
    build_latest(&engine, &explicit);
    build_latest(&engine, &wide);
    catalog.mark_queried(&explicit);
    catalog.mark_queried(&wide);
    thresholds(&engine, Duration::from_hours(1), Duration::from_hours(2), Duration::ZERO);

    run_maintenance(&engine, "tenant-a");
    run_maintenance(&engine, "tenant-a");
    let view = find_index(&engine, "tenant-a", "users/by-age");
    assert_eq!(view.kind(), IndexKind::Explicit);
    assert_eq!(view.state(), IndexLifecycleState::Active);
    assert!(view.transitions().is_empty(), "explicit indexes carry no auto lifecycle");

    // The query path never widens from an explicit partial candidate either:
    // a range query the restricted explicit index cannot serve creates the
    // demand's own auto identity, not one swallowing the explicit fields.
    let restricted = auto_namespace("tenant-a", "users/by-rank");
    catalog
        .register_explicit(
            &restricted,
            &explicit_definition("users", "rank", IndexCapabilities::EQUALITY),
        )
        .expect("registers");
    execute_dynamic(&engine, users_spec("tenant-a", predicate(vec![above("rank", 1)])), "tenant-a")
        .expect("executes");
    let record = last_record(&engine, "tenant-a");
    assert_eq!(
        chosen_name(&record),
        "Auto/users/ByRankAndAgeAndName",
        "the widening unions the partial AUTO candidates (V1's extend rule), never explicit fields"
    );
    let unchanged = find_index(&engine, "tenant-a", "users/by-rank");
    assert_eq!(unchanged.fields().len(), 1);
    assert_eq!(unchanged.fields()[0].name(), "rank");
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn the_auto_registry_is_bounded_per_tenant_and_refuses_fail_closed_when_full() {
    let (dir, engine) = open_engine("registry-bound");
    commit_users(&engine, "tenant-a", "op-1", &[5]);
    commit_users(&engine, "tenant-b", "op-2", &[6]);
    // One collection per shape keeps the surpass pass from merging the
    // fixtures into unions: the bound, not the merge rules, is under test.
    thresholds(&engine, Duration::from_hours(1), Duration::from_hours(2), Duration::from_hours(1));

    for index in 0..MAX_AUTO_INDEXES_PER_TENANT {
        let collection = format!("c{index}");
        execute_dynamic(
            &engine,
            dynamic_spec(
                "tenant-a",
                &collection,
                predicate(vec![equals("f", i64::try_from(index).expect("small"))]),
                Vec::new(),
            ),
            "tenant-a",
        )
        .expect("creates");
    }
    assert_eq!(
        definitions(&engine, "tenant-a").len(),
        MAX_AUTO_INDEXES_PER_TENANT,
        "the per-tenant auto registry is bounded"
    );

    // One more distinct shape fails closed with a recorded reason: no
    // unbounded growth and no silent unindexed scan.
    let error = execute_dynamic(
        &engine,
        dynamic_spec("tenant-a", "coverflow", predicate(vec![equals("overflow", 1)]), Vec::new()),
        "tenant-a",
    )
    .expect_err("the full registry refuses creation");
    assert_eq!(error, QueryError::IndexNotReady);
    assert_eq!(
        last_record(&engine, "tenant-a").outcome(),
        &SelectionOutcome::CreationRefused { reason: CreationRefusal::RegistryFull }
    );
    assert_eq!(
        definitions(&engine, "tenant-a").len(),
        MAX_AUTO_INDEXES_PER_TENANT,
        "the refusal created nothing"
    );

    // Another tenant's registry is independent: tenant-b still creates.
    execute_dynamic(&engine, users_spec("tenant-b", predicate(vec![equals("age", 6)])), "tenant-b")
        .expect("executes");
    assert_eq!(definitions(&engine, "tenant-b").len(), 1);

    // Make-room: once the tenant's auto indexes idle past the deletion
    // threshold, the retirement sweep frees the slots (demotion on one
    // pass, deletion on the next — V1's two observer cycles) and the same
    // query shape proceeds.
    thresholds(&engine, Duration::ZERO, Duration::ZERO, Duration::ZERO);
    advance_clock(&engine, Duration::from_secs(1));
    run_maintenance(&engine, "tenant-a");
    run_maintenance(&engine, "tenant-a");
    execute_dynamic(
        &engine,
        dynamic_spec("tenant-a", "coverflow", predicate(vec![equals("overflow", 1)]), Vec::new()),
        "tenant-a",
    )
    .expect("the retired slots make room for the creation");
    assert!(
        definitions(&engine, "tenant-a")
            .iter()
            .any(|index| index.name() == "Auto/coverflow/ByOverflow"),
        "the creation proceeded after make-room retirement"
    );
    assert!(
        definitions(&engine, "tenant-a").len() <= MAX_AUTO_INDEXES_PER_TENANT,
        "make-room never exceeds the bound"
    );
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn selection_record_storage_is_tenant_scoped_and_fair() {
    let (dir, engine) = open_engine("record-fairness");
    commit_users(&engine, "tenant-a", "op-1", &[5]);
    commit_users(&engine, "tenant-b", "op-2", &[6]);

    execute_dynamic(&engine, users_spec("tenant-b", predicate(vec![equals("age", 6)])), "tenant-b")
        .expect("executes");

    // Tenant-a floods the shared engine far past the per-tenant bound.
    let over = super::MAX_SELECTION_RECORDS + 10;
    for index in 0..over {
        execute_dynamic(
            &engine,
            users_spec(
                "tenant-a",
                predicate(vec![equals("age", i64::try_from(index % 5).expect("small"))]),
            ),
            "tenant-a",
        )
        .expect("executes");
    }
    assert_eq!(
        records(&engine, "tenant-a").len(),
        super::MAX_SELECTION_RECORDS,
        "one tenant's own store is bounded"
    );
    let before = records(&engine, "tenant-b");
    assert_eq!(before.len(), 1, "the flood never evicted another tenant's records");
    assert_eq!(before[0].tenant(), &tenant("tenant-b"));

    execute_dynamic(&engine, users_spec("tenant-b", predicate(vec![equals("age", 6)])), "tenant-b")
        .expect("executes");
    let after = records(&engine, "tenant-b");
    assert_eq!(after.len(), 2, "both of tenant-b's records survived the flood");
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn replacement_never_interrupts_queries_and_results_stay_identical() {
    let (dir, engine) = open_engine("no-interruption");
    commit_users(&engine, "tenant-a", "op-1", &[5, 5, 9]);
    let expected = ["doc-op-1-0", "doc-op-1-1"];

    let ids = |result: &QueryResult| {
        result.rows().iter().map(|row| row.document().id().as_str().to_owned()).collect::<Vec<_>>()
    };
    let narrow_query = || users_spec("tenant-a", predicate(vec![equals("age", 5)]));

    // Create the narrow index, then widen past it: the replacement window
    // spans the supersede scheduling, the drain, and the retirement.
    let first = execute_dynamic(&engine, narrow_query(), "tenant-a").expect("executes");
    assert_eq!(ids(&first), expected);
    execute_dynamic(
        &engine,
        users_spec("tenant-a", predicate(vec![equals("age", 5), equals("name", 3)])),
        "tenant-a",
    )
    .expect("executes");

    // During the drain the superseded index still serves, and every query
    // answers identically.
    thresholds(&engine, Duration::from_hours(1), Duration::from_hours(2), Duration::from_secs(30));
    let during = execute_dynamic(&engine, narrow_query(), "tenant-a").expect("no interruption");
    assert_eq!(ids(&during), expected);

    advance_clock(&engine, Duration::from_secs(31));
    let after = execute_dynamic(&engine, narrow_query(), "tenant-a").expect("no interruption");
    assert_eq!(ids(&after), expected);
    assert_eq!(
        find_index(&engine, "tenant-a", "Auto/users/ByAge").state(),
        IndexLifecycleState::Retired
    );

    // The retired index was the cached selection of this shape: the
    // retirement query itself already had to fall back to a full match —
    // a vanished index never serves from the cache again — and every later
    // query answers identically (the bounded retry, never an interruption).
    let record = last_record(&engine, "tenant-a");
    assert_eq!(chosen_name(&record), "Auto/users/ByAgeAndName");
    assert!(!record.cache_hit(), "a vanished index never serves from the cache again");
    let fallback = execute_dynamic(&engine, narrow_query(), "tenant-a").expect("no interruption");
    assert_eq!(ids(&fallback), expected);
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn a_crash_during_replacement_recovers_to_the_same_correct_state() {
    let (dir, engine) = open_engine("crash-replacement");
    commit_users(&engine, "tenant-a", "op-1", &[5, 5, 9]);
    let expected = ["doc-op-1-0", "doc-op-1-1"];
    let ids = |result: &QueryResult| {
        result.rows().iter().map(|row| row.document().id().as_str().to_owned()).collect::<Vec<_>>()
    };
    let narrow_query = || users_spec("tenant-a", predicate(vec![equals("age", 5)]));

    // Drive the lifecycle into the middle of a replacement: the covering
    // index exists, the superseded one is still draining.
    execute_dynamic(&engine, narrow_query(), "tenant-a").expect("executes");
    execute_dynamic(
        &engine,
        users_spec("tenant-a", predicate(vec![equals("age", 5), equals("name", 3)])),
        "tenant-a",
    )
    .expect("executes");
    assert_eq!(definitions(&engine, "tenant-a").len(), 2, "mid-replacement: both live");

    // Crash: the engine dies before the drain completes.
    drop(engine);

    // Recovery: reopen the same durable location. The in-memory catalog
    // re-derives — no half-replaced index ever serves — and the lifecycle
    // converges back to the same covering state with identical answers.
    let engine = reopen_engine(&dir);
    thresholds(&engine, Duration::from_hours(1), Duration::from_hours(2), Duration::ZERO);
    // The fresh catalog observes no publication until a commit arrives; one
    // non-matching document gives the re-derived builds their input without
    // changing any expected answer.
    commit_users(&engine, "tenant-a", "op-2", &[9]);
    let recovered = execute_dynamic(&engine, narrow_query(), "tenant-a").expect("executes");
    assert_eq!(ids(&recovered), expected, "the crash changed no answer");

    // The deterministic identity recreates the narrow index first, and the
    // next wider query supersedes it again; after the drain the tenant owns
    // exactly the covering index.
    execute_dynamic(
        &engine,
        users_spec("tenant-a", predicate(vec![equals("age", 5), equals("name", 3)])),
        "tenant-a",
    )
    .expect("executes");
    advance_clock(&engine, Duration::from_secs(1));
    let converged = execute_dynamic(&engine, narrow_query(), "tenant-a").expect("executes");
    assert_eq!(ids(&converged), expected);
    assert_eq!(
        states_of(&engine, "tenant-a"),
        vec![("Auto/users/ByAgeAndName".to_owned(), IndexLifecycleState::Active)],
        "the lifecycle re-derives to the same converged state"
    );
    let final_result = execute_dynamic(&engine, narrow_query(), "tenant-a").expect("executes");
    assert_eq!(ids(&final_result), expected);
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn lifecycle_truth_is_observable_through_the_typed_index_view() {
    let (dir, engine) = open_engine("typed-view");
    commit_users(&engine, "tenant-a", "op-1", &[5, 3]);

    // The registered-index view is the typed read: it carries the lifecycle
    // state and the bounded transition history of every index, live and
    // retired, including the reserved-namespace identity of auto indexes.
    execute_dynamic(&engine, users_spec("tenant-a", predicate(vec![equals("age", 5)])), "tenant-a")
        .expect("executes");
    let view = find_index(&engine, "tenant-a", "Auto/users/ByAge");
    assert!(view.name().starts_with(AUTO_NAMESPACE_PREFIX));
    assert_eq!(view.state(), IndexLifecycleState::Active);
    assert!(view.transitions().is_empty(), "no transition has fired yet");
    assert!(view.queried());

    // Retire it and observe the same view carry the recorded transitions.
    thresholds(&engine, Duration::ZERO, Duration::ZERO, Duration::ZERO);
    advance_clock(&engine, Duration::from_secs(1));
    run_maintenance(&engine, "tenant-a");
    run_maintenance(&engine, "tenant-a");
    let retired = find_index(&engine, "tenant-a", "Auto/users/ByAge");
    assert_eq!(retired.state(), IndexLifecycleState::Retired);
    assert_eq!(retired.transitions().len(), 2, "idle demotion then retirement, both recorded");
    assert_eq!(retired.transitions()[0].to(), IndexLifecycleState::Idle);
    assert_eq!(retired.transitions()[1].to(), IndexLifecycleState::Retired);
    assert_eq!(retired.collection(), "users");
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn engine_config_thresholds_and_the_engine_owned_tick_drive_maintenance() {
    // IX-03 rider, landed by the engine seam's owner: the thresholds come
    // from EngineConfig (honored at open, not a post-open test-only setter)
    // and the Engine exposes the maintenance tick as the timer body a host
    // schedules (PD-2026-08-26-01 D1: the host owns cadence, the Engine owns
    // the pass). The query path stays the live driver; this test proves the
    // tick alone advances the same state machine with no query in sight.
    let dir = location("engine-tick");
    std::fs::remove_dir_all(&dir).ok();
    let zero = LifecycleThresholds::new(Duration::ZERO, Duration::ZERO, Duration::ZERO);
    let engine = Engine::open(
        EngineConfig::durable(&dir).expect("durable location").with_lifecycle_thresholds(zero),
    )
    .expect("engine opens");
    assert_eq!(
        engine.index_catalog().lifecycle_thresholds(),
        zero,
        "the Engine honors the configured thresholds at open, not the default"
    );

    commit_users(&engine, "tenant-a", "op-1", &[5, 3]);
    execute_dynamic(&engine, users_spec("tenant-a", predicate(vec![equals("age", 5)])), "tenant-a")
        .expect("executes");
    assert_eq!(
        find_index(&engine, "tenant-a", "Auto/users/ByAge").state(),
        IndexLifecycleState::Active
    );

    // Two ticks, no queries: the first demotes past the zero idle gate, the
    // second retires past the zero deletion gate — the full machine through
    // the Engine-owned body alone.
    engine.maintain_indexes();
    assert_eq!(
        find_index(&engine, "tenant-a", "Auto/users/ByAge").state(),
        IndexLifecycleState::Idle,
        "the first tick demotes the unqueried index"
    );
    engine.maintain_indexes();
    let retired = find_index(&engine, "tenant-a", "Auto/users/ByAge");
    assert_eq!(retired.state(), IndexLifecycleState::Retired);
    assert_eq!(retired.transitions().len(), 2, "both transitions recorded by the tick alone");
    assert_eq!(retired.transitions()[0].to(), IndexLifecycleState::Idle);
    assert_eq!(retired.transitions()[1].to(), IndexLifecycleState::Retired);
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}
