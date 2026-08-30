//! In-crate optimizer tests covering the IX-02 contract list.
//!
//! Every test crosses the public seam: plans compile through
//! [`QueryStore`](super::super::QueryStore), execute against real snapshots of
//! a durable Engine, and the optimizer's decisions are observed through the
//! typed reads on the store. Catalog setup that has no public operation yet
//! (explicit registration, rebuilds, the kill switch) drives the Engine-owned
//! catalog through its crate-private surface, exactly as the W3-2 index tests
//! drive publication. Nothing here claims a V1 parity row, a speed result, or
//! an index-accelerated execution path.

use std::{collections::BTreeMap, path::PathBuf, time::Duration};

use super::{
    AUTO_NAMESPACE_PREFIX, CandidateNote, CandidateVerdict, CreationRefusal, FailureReason,
    FirstBuildOutcome, IndexCapabilities, IndexCapability, IndexDefinition, IndexField, IndexKind,
    IndexSelectionRecord, MAX_SELECTION_RECORDS, PartialReason, RegisteredIndex, SelectionOutcome,
    TieBreak, auto_index_name,
};
use crate::query::{
    IndexNamespace, OrderDirection, OrderingKey, QueryCondition, QueryError, QueryOrdering,
    QueryPaging, QueryPredicate, QueryProjection, QueryResult, QuerySource, QuerySpec,
    StalenessMode, StalenessPolicy,
};
use crate::{
    engine::{CommitOutcome, Engine, EngineConfig},
    index::{IndexBuilder, IndexError},
    operations::{QueryInput, TqlQueryText},
    types::{
        Cancellation, CollectionName, Deadline, Document, DocumentId, DocumentValue,
        ExpectedVersion, LocalDurability, OperationId, TenantId, TenantScope,
    },
};

fn location(tag: &str) -> PathBuf {
    std::env::temp_dir().join(format!("terrabase-ix02-optimizer-{tag}-{}", std::process::id()))
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

fn commit_users(engine: &Engine, tenant_name: &str, tag: &str, ages: &[i64]) -> u64 {
    let documents = ages
        .iter()
        .enumerate()
        .map(|(index, age)| {
            object_document(
                &format!("doc-{tag}-{index}"),
                "users",
                &[("age", DocumentValue::Integer(*age))],
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
    predicate: QueryPredicate,
    ordering_keys: Vec<(&str, OrderDirection)>,
) -> QuerySpec {
    QuerySpec::try_new(
        tenant(claim),
        source("users"),
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

/// A named-index specification: the policy requires one namespace, which
/// bypasses the optimizer entirely (AX-26).
fn named_spec(claim: &str, required: IndexNamespace) -> QuerySpec {
    QuerySpec::try_new(
        tenant(claim),
        source("users"),
        predicate(Vec::new()),
        QueryOrdering::default(),
        QueryProjection::whole_document(),
        QueryPaging::try_new(0, None).expect("valid paging"),
        StalenessPolicy::try_new(StalenessMode::AllowStale, vec![required]).expect("valid policy"),
        Deadline::after(Duration::from_secs(30)).expect("valid deadline"),
        Cancellation::new(),
    )
    .expect("valid specification")
}

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

fn records(engine: &Engine, tenant_name: &str) -> Vec<IndexSelectionRecord> {
    engine.queries().recent_index_selections(&tenant(tenant_name))
}

fn definitions(engine: &Engine, tenant_name: &str) -> Vec<RegisteredIndex> {
    engine.queries().registered_indexes(&tenant(tenant_name))
}

/// Returns the newest selection record of one tenant.
fn last_record(engine: &Engine, tenant_name: &str) -> IndexSelectionRecord {
    let all = records(engine, tenant_name);
    all.last().expect("at least one record").clone()
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

fn explicit_field(field: &str, capabilities: IndexCapabilities) -> IndexField {
    IndexField::try_new(field, capabilities).expect("valid field")
}

fn chosen_name(record: &IndexSelectionRecord) -> String {
    match record.outcome() {
        SelectionOutcome::ChosenIndex { name, .. }
        | SelectionOutcome::CreatedAutoIndex { name, .. } => name.clone(),
        other => panic!("expected a serving index, got {other:?}"),
    }
}

// ---------------------------------------------------------------------------
// The IX-02 red-test list
// ---------------------------------------------------------------------------

#[test]
fn every_dynamic_query_records_its_selection_with_candidates_and_staleness_basis() {
    let (dir, engine) = open_engine("record-selection");
    commit_users(&engine, "tenant-a", "op-1", &[3, 5]);

    let result = execute_dynamic(
        &engine,
        dynamic_spec("tenant-a", predicate(vec![equals("age", 5)]), Vec::new()),
        "tenant-a",
    )
    .expect("executes");
    assert_eq!(result.matched(), 1, "the scan answer stays correct");

    let records = records(&engine, "tenant-a");
    assert_eq!(records.len(), 1, "one dynamic execution produces one record");
    let record = &records[0];
    assert_eq!(record.tenant(), &tenant("tenant-a"));
    assert_eq!(record.collection(), "users");
    assert_eq!(record.query_fields().len(), 1);
    assert_eq!(record.query_fields()[0].name(), "age");
    assert!(
        record.query_fields()[0].capabilities().has(IndexCapability::Equality),
        "an equality condition demands the equality capability"
    );
    assert!(!record.cache_hit(), "the first selection is a full match");
    assert!(
        matches!(record.outcome(), SelectionOutcome::CreatedAutoIndex { .. }),
        "no index served, so one was created: {:?}",
        record.outcome()
    );
    let basis = record.staleness_basis().expect("the answer carries a staleness basis");
    assert!(basis.available().is_some(), "the created index built before the record closed");
    assert!(
        record.candidates().is_empty(),
        "the tenant had no definitions before this query created one"
    );
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn named_index_plans_bypass_the_optimizer_entirely() {
    let (dir, engine) = open_engine("named-bypass");
    commit_users(&engine, "tenant-a", "op-1", &[1]);
    let catalog = engine.index_catalog();
    let required = IndexNamespace::try_new(tenant("tenant-a"), "by-age").expect("valid namespace");
    assert_eq!(catalog.ensure_namespace(&required, "age"), 1);

    // A named-index plan executes and records nothing: naming an index is the
    // structural bypass (AX-26).
    let result = execute_dynamic(&engine, named_spec("tenant-a", required.clone()), "tenant-a");
    assert!(result.is_ok());
    assert!(
        records(&engine, "tenant-a").is_empty(),
        "a named plan must not produce a selection record"
    );
    assert!(definitions(&engine, "tenant-a").is_empty(), "no definition was created either");

    // The dynamic route on the same store does record.
    execute_dynamic(
        &engine,
        dynamic_spec("tenant-a", predicate(vec![equals("age", 1)]), Vec::new()),
        "tenant-a",
    )
    .expect("executes");
    assert_eq!(records(&engine, "tenant-a").len(), 1);
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn unmatched_dynamic_query_creates_a_deterministic_auto_index() {
    let (dir, engine) = open_engine("auto-creation");
    commit_users(&engine, "tenant-a", "op-1", &[5]);

    let result = execute_dynamic(
        &engine,
        dynamic_spec("tenant-a", predicate(vec![equals("age", 5)]), Vec::new()),
        "tenant-a",
    )
    .expect("executes");
    assert_eq!(result.matched(), 1);

    let registered = definitions(&engine, "tenant-a");
    assert_eq!(registered.len(), 1, "the unmatched query created exactly one index");
    let created = &registered[0];
    assert_eq!(created.name(), "Auto/users/ByAge");
    assert_eq!(created.kind(), IndexKind::Auto);
    assert_eq!(created.collection(), "users");
    assert_eq!(created.fields().len(), 1);
    assert_eq!(created.fields()[0].name(), "age");

    // The second identical query selects the created index; nothing new is
    // created, no creation event is recorded, and the identical shape is
    // served from the bounded selection cache with its origin recorded.
    let result = execute_dynamic(
        &engine,
        dynamic_spec("tenant-a", predicate(vec![equals("age", 5)]), Vec::new()),
        "tenant-a",
    )
    .expect("executes");
    assert_eq!(result.matched(), 1);
    assert_eq!(definitions(&engine, "tenant-a").len(), 1);
    let records = records(&engine, "tenant-a");
    assert_eq!(records.len(), 2);
    assert_eq!(
        records[1].outcome(),
        &SelectionOutcome::ChosenIndex {
            name: "Auto/users/ByAge".to_owned(),
            kind: IndexKind::Auto,
        }
    );
    assert!(records[1].cache_hit(), "the identical shape is a recorded cache hit");
    assert!(
        records[1].staleness_basis().is_some(),
        "the cached selection still carries its staleness basis"
    );

    // Determinism: a second engine derives the identical name from the
    // identical query shape.
    let (other_dir, other_engine) = open_engine("auto-creation-determinism");
    commit_users(&other_engine, "tenant-a", "op-1", &[5]);
    execute_dynamic(
        &other_engine,
        dynamic_spec("tenant-a", predicate(vec![equals("age", 5)]), Vec::new()),
        "tenant-a",
    )
    .expect("executes");
    assert_eq!(
        definitions(&other_engine, "tenant-a")[0].name(),
        created.name(),
        "the Auto/ name is a pure function of the query shape"
    );
    drop(engine);
    drop(other_engine);
    std::fs::remove_dir_all(&dir).ok();
    std::fs::remove_dir_all(&other_dir).ok();
}

#[test]
fn auto_index_names_are_canonical_with_hash_truncation() {
    // The canonical field order is the ordinal order of the query shape:
    // where-clause fields in declaration order, then order-by fields.
    assert_eq!(auto_index_name(&auto_definition("users", &["age"])), "Auto/users/ByAge");
    assert_eq!(
        auto_index_name(&auto_definition("users", &["name", "age"])),
        "Auto/users/ByNameAndAge",
        "declaration order is canonical, not alphabetical"
    );
    assert_eq!(auto_index_name(&auto_definition("events", &["at"])), "Auto/events/ByAt");
    assert_ne!(
        auto_index_name(&auto_definition("users", &["age", "name"])),
        auto_index_name(&auto_definition("users", &["name", "age"])),
        "different shapes keep different identities"
    );

    // Long shapes truncate with an ellipsis and a stable hash tail, and stay
    // inside the namespace name bound.
    let long_field = "f".repeat(240);
    let long = auto_index_name(&auto_definition("users", &[long_field.as_str(), "age"]));
    assert!(long.len() <= 255, "truncated names respect the field byte bound: {}", long.len());
    assert!(long.starts_with(AUTO_NAMESPACE_PREFIX));
    assert!(long.contains("..."), "the truncation is observable");
    let tail: String = long.chars().rev().take(16).collect::<String>().chars().rev().collect();
    assert!(
        tail.chars().all(|character| character.is_ascii_hexdigit()),
        "the tail is a 16-digit hex digest: {long}"
    );
    assert_eq!(
        auto_index_name(&auto_definition("users", &[long_field.as_str(), "age"])),
        long,
        "the truncation is deterministic"
    );
    assert_ne!(
        auto_index_name(&auto_definition("users", &[long_field.as_str(), "name"])),
        long,
        "different long shapes hash apart"
    );
    assert!(IndexNamespace::try_new(tenant("tenant-a"), &long).is_ok());
}

#[test]
fn explicit_equivalent_index_takes_precedence_over_auto() {
    let (dir, engine) = open_engine("explicit-precedence");
    commit_users(&engine, "tenant-a", "op-1", &[5]);

    // Phase one: the dynamic query creates the auto index.
    execute_dynamic(
        &engine,
        dynamic_spec("tenant-a", predicate(vec![equals("age", 5)]), Vec::new()),
        "tenant-a",
    )
    .expect("executes");
    assert_eq!(definitions(&engine, "tenant-a").len(), 1);

    // Phase two: an equivalent explicit index is registered. The next dynamic
    // query must prefer it (D-3) and record why the auto candidate lost.
    let catalog = engine.index_catalog();
    let explicit = IndexDefinition::try_new(
        CollectionName::try_new("users").expect("valid identifier"),
        vec![explicit_field("age", IndexCapabilities::FULL)],
        IndexKind::Explicit,
    )
    .expect("valid definition");
    let namespace =
        IndexNamespace::try_new(tenant("tenant-a"), "users/by-age").expect("valid namespace");
    assert_eq!(catalog.register_explicit(&namespace, &explicit), Ok(1));

    execute_dynamic(
        &engine,
        dynamic_spec("tenant-a", predicate(vec![equals("age", 5)]), Vec::new()),
        "tenant-a",
    )
    .expect("executes");
    assert_eq!(definitions(&engine, "tenant-a").len(), 2, "no third index appears");
    let record = last_record(&engine, "tenant-a");
    assert_eq!(
        record.outcome(),
        &SelectionOutcome::ChosenIndex {
            name: "users/by-age".to_owned(),
            kind: IndexKind::Explicit
        },
        "an equivalent explicit index wins even though it never published"
    );
    assert!(record.tie_breaks().contains(&TieBreak::ExplicitPreferred));
    let auto_candidate = record
        .candidates()
        .iter()
        .find(|candidate| candidate.name() == "Auto/users/ByAge")
        .expect("the auto index stays a recorded candidate");
    assert_eq!(auto_candidate.verdict(), &CandidateVerdict::Complete);
    assert_eq!(auto_candidate.note(), Some(CandidateNote::ExplicitPreferredOverAuto));
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn explicit_registration_refuses_the_reserved_auto_namespace() {
    let (dir, engine) = open_engine("reserved-namespace");
    let catalog = engine.index_catalog();
    let definition = IndexDefinition::try_new(
        CollectionName::try_new("users").expect("valid identifier"),
        vec![explicit_field("age", IndexCapabilities::FULL)],
        IndexKind::Explicit,
    )
    .expect("valid definition");
    let reserved = IndexNamespace::try_new(tenant("tenant-a"), "Auto/users/ByAge")
        .expect("the name itself is a valid identifier");
    assert_eq!(
        catalog.register_explicit(&reserved, &definition),
        Err(IndexError::AutoNamespaceReserved),
        "explicit registration can never occupy the Auto/ namespace (AX-10)"
    );
    assert!(definitions(&engine, "tenant-a").is_empty());
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn partial_verdicts_record_their_reason_and_creation_serves_the_demand() {
    let (dir, engine) = open_engine("partial-verdicts");
    commit_users(&engine, "tenant-a", "op-1", &[5]);

    // A restricted explicit index on age (equality only) cannot serve a range
    // query: the verdict is partial with the capability reason.
    let catalog = engine.index_catalog();
    let restricted = IndexDefinition::try_new(
        CollectionName::try_new("users").expect("valid identifier"),
        vec![explicit_field("age", IndexCapabilities::EQUALITY)],
        IndexKind::Explicit,
    )
    .expect("valid definition");
    let namespace =
        IndexNamespace::try_new(tenant("tenant-a"), "users/by-age-eq").expect("valid namespace");
    assert_eq!(catalog.register_explicit(&namespace, &restricted), Ok(1));

    execute_dynamic(
        &engine,
        dynamic_spec("tenant-a", predicate(vec![above("age", 3)]), Vec::new()),
        "tenant-a",
    )
    .expect("executes");
    let record = last_record(&engine, "tenant-a");
    assert_eq!(
        chosen_name(&record),
        "Auto/users/ByAge",
        "the demand drove a creation, not the restricted index"
    );
    assert!(
        record.query_fields()[0].capabilities().has(super::IndexCapability::Ordering),
        "a range condition demands the ordering capability"
    );
    assert_eq!(
        record
            .candidates()
            .iter()
            .find(|candidate| candidate.name() == "users/by-age-eq")
            .expect("the restricted index is a candidate")
            .verdict(),
        &CandidateVerdict::Partial(PartialReason::CapabilityInsufficient {
            field: "age".to_owned(),
            required: IndexCapability::Ordering,
            present: IndexCapabilities::EQUALITY,
        })
    );

    // A field the candidate does not index at all is the other partial reason.
    execute_dynamic(
        &engine,
        dynamic_spec("tenant-a", predicate(vec![equals("age", 5), equals("name", 7)]), Vec::new()),
        "tenant-a",
    )
    .expect("executes");
    let record = last_record(&engine, "tenant-a");
    assert_eq!(chosen_name(&record), "Auto/users/ByAgeAndName");
    assert_eq!(
        record
            .candidates()
            .iter()
            .find(|candidate| candidate.name() == "users/by-age-eq")
            .expect("still a candidate")
            .verdict(),
        &CandidateVerdict::Partial(PartialReason::FieldNotIndexed("name".to_owned()))
    );
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn another_collections_definitions_are_failure_candidates() {
    let (dir, engine) = open_engine("failure-verdicts");
    commit_users(&engine, "tenant-a", "op-1", &[5]);
    let catalog = engine.index_catalog();
    let other = IndexDefinition::try_new(
        CollectionName::try_new("events").expect("valid identifier"),
        vec![explicit_field("age", IndexCapabilities::FULL)],
        IndexKind::Explicit,
    )
    .expect("valid definition");
    let namespace =
        IndexNamespace::try_new(tenant("tenant-a"), "events/by-age").expect("valid namespace");
    assert_eq!(catalog.register_explicit(&namespace, &other), Ok(1));

    execute_dynamic(
        &engine,
        dynamic_spec("tenant-a", predicate(vec![equals("age", 5)]), Vec::new()),
        "tenant-a",
    )
    .expect("executes");
    let record = last_record(&engine, "tenant-a");
    assert_eq!(
        record
            .candidates()
            .iter()
            .find(|candidate| candidate.name() == "events/by-age")
            .expect("every tenant definition is considered")
            .verdict(),
        &CandidateVerdict::Failure(FailureReason::AnotherCollection)
    );
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn kill_switch_fails_closed_with_a_typed_error_and_no_creation() {
    let (dir, engine) = open_engine("kill-switch");
    commit_users(&engine, "tenant-a", "op-1", &[5]);

    // A served query still works with creation disabled.
    execute_dynamic(
        &engine,
        dynamic_spec("tenant-a", predicate(vec![equals("age", 5)]), Vec::new()),
        "tenant-a",
    )
    .expect("executes");
    let catalog = engine.index_catalog();
    catalog.set_auto_index_creation(false);

    // An unmatched dynamic query fails closed: typed error, no scan silence,
    // and no index created.
    let error = execute_dynamic(
        &engine,
        dynamic_spec("tenant-a", predicate(vec![equals("name", 1)]), Vec::new()),
        "tenant-a",
    )
    .expect_err("creation is disabled, so the query must fail closed");
    assert_eq!(error, QueryError::IndexNotReady, "the kill switch reuses the frozen error set");
    assert_eq!(definitions(&engine, "tenant-a").len(), 1, "nothing was created");
    let record = last_record(&engine, "tenant-a");
    assert_eq!(
        record.outcome(),
        &SelectionOutcome::CreationRefused { reason: CreationRefusal::Disabled }
    );

    // The served shape and the named route stay unaffected.
    execute_dynamic(
        &engine,
        dynamic_spec("tenant-a", predicate(vec![equals("age", 5)]), Vec::new()),
        "tenant-a",
    )
    .expect("a serving index needs no creation");
    let required = IndexNamespace::try_new(tenant("tenant-a"), "by-age").expect("valid namespace");
    assert_eq!(catalog.ensure_namespace(&required, "age"), 1);
    execute_dynamic(&engine, named_spec("tenant-a", required), "tenant-a").expect("named route");
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn auto_creation_fails_closed_on_an_identity_conflict() {
    let (dir, engine) = open_engine("identity-conflict");
    commit_users(&engine, "tenant-a", "op-1", &[5]);
    // A raw namespace squats on the deterministic auto name with different
    // content: creation must fail closed instead of reusing or overwriting it.
    let catalog = engine.index_catalog();
    let squatter =
        IndexNamespace::try_new(tenant("tenant-a"), "Auto/users/ByAge").expect("valid namespace");
    assert_eq!(catalog.ensure_namespace(&squatter, "name"), 1);

    let error = execute_dynamic(
        &engine,
        dynamic_spec("tenant-a", predicate(vec![equals("age", 5)]), Vec::new()),
        "tenant-a",
    )
    .expect_err("the identity conflict is a fail-closed creation refusal");
    assert_eq!(error, QueryError::IndexNotReady);
    let record = last_record(&engine, "tenant-a");
    assert_eq!(
        record.outcome(),
        &SelectionOutcome::CreationRefused { reason: CreationRefusal::IdentityConflict }
    );
    // The squatter namespace keeps its own field; nothing was overwritten.
    let builder = IndexBuilder::new(&catalog, &engine);
    assert!(builder.rebuild(&squatter, engine.identity()).is_ok());
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn query_results_are_identical_indexed_and_unindexed() {
    let (dir, engine) = open_engine("result-identity");
    commit_users(&engine, "tenant-a", "op-1", &[5, 1, 9, 3, 7]);
    let (fresh_dir, fresh_engine) = open_engine("result-identity-unindexed");
    commit_users(&fresh_engine, "tenant-a", "op-1", &[5, 1, 9, 3, 7]);

    let shape = |store: &crate::query::QueryStore<'_>, snapshot: &crate::types::ReadSnapshot| {
        let plan = store
            .compile(QueryInput::typed(dynamic_spec(
                "tenant-a",
                predicate(vec![above("age", 2)]),
                vec![("age", OrderDirection::Descending)],
            )))
            .expect("compiles");
        let result = store.execute(&plan, snapshot).expect("executes");
        (
            result
                .rows()
                .iter()
                .map(|row| row.document().id().as_str().to_owned())
                .collect::<Vec<_>>(),
            result.matched(),
            result.ordering().clone(),
        )
    };

    // Unindexed: the first execution on a fresh engine (before any index can
    // exist) is the reference answer.
    let snapshot = fresh_engine.read_snapshot(&scope(&fresh_engine, "tenant-a")).expect("snapshot");
    let unindexed = shape(&fresh_engine.queries(), &snapshot);

    // First query on the other engine creates the auto index (unindexed run).
    let snapshot = engine.read_snapshot(&scope(&engine, "tenant-a")).expect("snapshot");
    let creating = shape(&engine.queries(), &snapshot);
    // Second query selects the created index.
    let snapshot = engine.read_snapshot(&scope(&engine, "tenant-a")).expect("snapshot");
    let indexed = shape(&engine.queries(), &snapshot);

    assert_eq!(creating, unindexed, "the creating first query answers identically");
    assert_eq!(indexed, unindexed, "the selected-index query answers identically");
    assert_eq!(unindexed.0, ["doc-op-1-2", "doc-op-1-4", "doc-op-1-0", "doc-op-1-3"]);
    drop(engine);
    drop(fresh_engine);
    std::fs::remove_dir_all(&dir).ok();
    std::fs::remove_dir_all(&fresh_dir).ok();
}

#[test]
fn first_query_behavior_is_bounded_and_recorded() {
    let (dir, engine) = open_engine("first-query");
    let covered = commit_users(&engine, "tenant-a", "op-1", &[5, 3]);

    // The triggering query runs to a correct answer while its index is being
    // created: bounded (one scan plus one initial build) and fully recorded.
    let result = execute_dynamic(
        &engine,
        dynamic_spec("tenant-a", predicate(vec![equals("age", 5)]), Vec::new()),
        "tenant-a",
    )
    .expect("executes");
    assert_eq!(result.matched(), 1);
    let record = last_record(&engine, "tenant-a");
    let SelectionOutcome::CreatedAutoIndex { name, build } = record.outcome() else {
        panic!("the first query records its creation event: {:?}", record.outcome());
    };
    assert_eq!(name.as_str(), "Auto/users/ByAge");
    assert_eq!(
        build,
        &FirstBuildOutcome::Published { epoch: 1, generation: 1, covered_sequence: covered },
        "the initial build publishes at the observed publication"
    );
    let basis = record.staleness_basis().expect("basis");
    assert!(basis.observed().is_some() && basis.available().is_some());

    // A query before any publication is also bounded and recorded: the build
    // is deferred, not invented.
    let (empty_dir, empty_engine) = open_engine("first-query-no-publication");
    execute_dynamic(
        &empty_engine,
        dynamic_spec("tenant-a", predicate(vec![equals("age", 5)]), Vec::new()),
        "tenant-a",
    )
    .expect("executes");
    let record = last_record(&empty_engine, "tenant-a");
    let SelectionOutcome::CreatedAutoIndex { build, .. } = record.outcome() else {
        panic!("creation is recorded");
    };
    assert_eq!(build, &FirstBuildOutcome::NoPublicationObserved);
    drop(engine);
    drop(empty_engine);
    std::fs::remove_dir_all(&dir).ok();
    std::fs::remove_dir_all(&empty_dir).ok();
}

#[test]
fn concurrent_cold_start_queries_all_answer_correctly() {
    let (dir, engine) = open_engine("cold-start-herd");
    commit_users(&engine, "tenant-a", "op-1", &[5, 3, 5]);

    // Sixteen threads issue the same equality query against one fresh engine
    // whose catalog holds no index for the shape yet. Exactly one
    // registration creates the auto index and runs its initial build; the
    // others select the registered namespace and serve their full correct
    // answer through the bounded scan. Every thread must answer with the
    // same correct result, no thread may panic, and no engine or catalog
    // lock may be poisoned (a poisoned lock would fail every later call).
    let spec = dynamic_spec("tenant-a", predicate(vec![equals("age", 5)]), Vec::new());
    std::thread::scope(|thread_scope| {
        let mut joined = Vec::new();
        for _ in 0..16 {
            let spec = spec.clone();
            joined.push(thread_scope.spawn(|| {
                let store = engine.queries();
                let plan = store.compile(QueryInput::typed(spec)).expect("compiles");
                let snapshot = engine.read_snapshot(&scope(&engine, "tenant-a")).expect("snapshot");
                store.execute(&plan, &snapshot)
            }));
        }
        for handle in joined {
            let result = handle.join().expect("a cold-start query thread panicked");
            let result = result.expect("a cold-start query executes");
            let ids: Vec<&str> =
                result.rows().iter().map(|row| row.document().id().as_str()).collect();
            assert_eq!(ids, ["doc-op-1-0", "doc-op-1-2"], "every thread answers correctly");
            assert_eq!(result.matched(), 2);
        }
    });

    // The catalog is intact after the herd: the index exists, published once,
    // and a later query still selects it.
    let registered = definitions(&engine, "tenant-a");
    assert_eq!(registered.len(), 1, "one cold shape registers one index");
    assert_eq!(registered[0].name(), "Auto/users/ByAge");
    assert_eq!(registered[0].published().map(ObservedGeneration::generation), Some(1));
    execute_dynamic(
        &engine,
        dynamic_spec("tenant-a", predicate(vec![equals("age", 5)]), Vec::new()),
        "tenant-a",
    )
    .expect("the engine still serves the shape after the herd");
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn selection_records_are_bounded_engine_state() {
    let (dir, engine) = open_engine("record-bound");
    commit_users(&engine, "tenant-a", "op-1", &[5]);
    let over = super::MAX_SELECTION_RECORDS + 10;
    for index in 0..over {
        execute_dynamic(
            &engine,
            dynamic_spec(
                "tenant-a",
                predicate(vec![equals("age", i64::try_from(index % 5).expect("small"))]),
                Vec::new(),
            ),
            "tenant-a",
        )
        .expect("executes");
    }
    let records = records(&engine, "tenant-a");
    assert_eq!(records.len(), MAX_SELECTION_RECORDS, "the record store is bounded");
    // The retained window is the newest one: the last record is the last query.
    let last = records.last().expect("non-empty");
    assert_eq!(
        last.query_fields().last().map(IndexField::name),
        Some("age"),
        "the newest record is retained"
    );
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn worse_than_available_index_needs_a_recorded_reason() {
    let (dir, engine) = open_engine("tie-breaks");
    let catalog = engine.index_catalog();
    commit_users(&engine, "tenant-a", "op-1", &[5]);
    let first = catalog.delta().expect("publication observed");
    commit_users(&engine, "tenant-a", "op-2", &[6]);
    let second = catalog.delta().expect("publication observed");

    // Two complete candidates: a narrow one kept up to date, and a wider one
    // that published at an older coverage.
    let tenant_a = tenant("tenant-a");
    let narrow =
        catalog.register_auto(&tenant_a, &auto_definition("users", &["age"])).expect("registers");
    let wide = catalog
        .register_auto(&tenant_a, &auto_definition("users", &["age", "name"]))
        .expect("registers");
    let builder = IndexBuilder::new(&catalog, &engine);
    assert!(builder.rebuild_from(narrow.namespace(), &second, engine.identity()).is_ok());
    assert!(builder.rebuild_from(wide.namespace(), &first, engine.identity()).is_ok());

    execute_dynamic(
        &engine,
        dynamic_spec("tenant-a", predicate(vec![equals("age", 5)]), Vec::new()),
        "tenant-a",
    )
    .expect("executes");
    let record = last_record(&engine, "tenant-a");
    assert_eq!(chosen_name(&record), "Auto/users/ByAge", "the most up-to-date candidate wins");
    assert!(record.tie_breaks().contains(&TieBreak::MostUpToDate));
    assert_eq!(
        record
            .candidates()
            .iter()
            .find(|candidate| candidate.name() == "Auto/users/ByAgeAndName")
            .expect("the loser stays visible")
            .note(),
        Some(CandidateNote::NotMostUpToDate),
        "a query served by the better index must say why the other lost"
    );

    // Once the wide candidate catches up, widest wins and the reason flips.
    assert!(builder.rebuild_from(wide.namespace(), &second, engine.identity()).is_ok());
    execute_dynamic(
        &engine,
        dynamic_spec("tenant-a", predicate(vec![equals("age", 5)]), Vec::new()),
        "tenant-a",
    )
    .expect("executes");
    let record = last_record(&engine, "tenant-a");
    assert_eq!(chosen_name(&record), "Auto/users/ByAgeAndName", "at equal recency the widest wins");
    assert!(record.tie_breaks().contains(&TieBreak::Widest));
    assert_eq!(
        record
            .candidates()
            .iter()
            .find(|candidate| candidate.name() == "Auto/users/ByAge")
            .expect("still visible")
            .note(),
        Some(CandidateNote::NotWidest)
    );
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn plan_cache_hits_are_recorded_and_invalidate_on_catalog_change() {
    let (dir, engine) = open_engine("plan-cache");
    commit_users(&engine, "tenant-a", "op-1", &[5]);
    let shape = dynamic_spec("tenant-a", predicate(vec![equals("age", 5)]), Vec::new());

    execute_dynamic(&engine, shape.clone(), "tenant-a").expect("creates");
    execute_dynamic(&engine, shape.clone(), "tenant-a").expect("selects");
    let records = records(&engine, "tenant-a");
    assert_eq!(records.len(), 2);
    assert!(!records[0].cache_hit());
    assert!(
        records[1].cache_hit(),
        "the identical shape is a recorded cache hit, and the origin is the record"
    );
    assert_eq!(chosen_name(&records[1]), "Auto/users/ByAge");

    // A catalog change invalidates the cached selection: registering an
    // explicit equivalent index forces a full match that prefers it.
    let catalog = engine.index_catalog();
    let explicit = IndexDefinition::try_new(
        CollectionName::try_new("users").expect("valid identifier"),
        vec![explicit_field("age", IndexCapabilities::FULL)],
        IndexKind::Explicit,
    )
    .expect("valid definition");
    let namespace =
        IndexNamespace::try_new(tenant("tenant-a"), "users/by-age").expect("valid namespace");
    assert_eq!(catalog.register_explicit(&namespace, &explicit), Ok(1));
    execute_dynamic(&engine, shape, "tenant-a").expect("selects");
    let record = last_record(&engine, "tenant-a");
    assert!(!record.cache_hit(), "the catalog change invalidated the entry");
    assert_eq!(chosen_name(&record), "users/by-age");
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn tql_text_lowers_through_the_optimizer_seam() {
    let (dir, engine) = open_engine("v1-lowering");
    commit_users(&engine, "tenant-a", "op-1", &[5]);
    let store = engine.queries();
    let text = TqlQueryText::try_new("from users where age = 5").expect("valid text");
    let plan = store.compile(QueryInput::tql(text)).expect("compiles");
    let snapshot = engine.read_snapshot(&scope(&engine, "tenant-a")).expect("snapshot");
    let result = store.execute(&plan, &snapshot).expect("executes");
    assert_eq!(result.matched(), 1);
    let records = records(&engine, "tenant-a");
    assert_eq!(records.len(), 1, "the lowered plan selects through the same seam");
    assert_eq!(chosen_name(&records[0]), "Auto/users/ByAge");

    // The typed form of the identical shape lands on the identical identity.
    let result = execute_dynamic(
        &engine,
        dynamic_spec("tenant-a", predicate(vec![equals("age", 5)]), Vec::new()),
        "tenant-a",
    )
    .expect("executes");
    assert_eq!(result.matched(), 1);
    assert_eq!(definitions(&engine, "tenant-a").len(), 1);
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn collection_scan_queries_create_no_index_and_record_the_scan() {
    let (dir, engine) = open_engine("collection-scan");
    commit_users(&engine, "tenant-a", "op-1", &[5, 3]);
    let result = execute_dynamic(
        &engine,
        dynamic_spec("tenant-a", predicate(Vec::new()), Vec::new()),
        "tenant-a",
    )
    .expect("executes");
    assert_eq!(result.matched(), 2);
    let records = records(&engine, "tenant-a");
    assert_eq!(records.len(), 1);
    assert_eq!(records[0].outcome(), &SelectionOutcome::CollectionScan);
    assert!(records[0].candidates().is_empty(), "nothing was considered for a pure scan");
    assert!(records[0].staleness_basis().is_none());
    assert!(definitions(&engine, "tenant-a").is_empty(), "a pure scan creates no index (AX-1)");
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn selection_marks_the_serving_index_queried() {
    let (dir, engine) = open_engine("mark-queried");
    commit_users(&engine, "tenant-a", "op-1", &[5]);
    let catalog = engine.index_catalog();
    let explicit = IndexDefinition::try_new(
        CollectionName::try_new("users").expect("valid identifier"),
        vec![explicit_field("age", IndexCapabilities::FULL)],
        IndexKind::Explicit,
    )
    .expect("valid definition");
    let namespace =
        IndexNamespace::try_new(tenant("tenant-a"), "users/by-age").expect("valid namespace");
    assert_eq!(catalog.register_explicit(&namespace, &explicit), Ok(1));
    assert!(
        !definitions(&engine, "tenant-a")[0].queried(),
        "a freshly registered index is unqueried"
    );
    execute_dynamic(
        &engine,
        dynamic_spec("tenant-a", predicate(vec![equals("age", 5)]), Vec::new()),
        "tenant-a",
    )
    .expect("executes");
    assert!(definitions(&engine, "tenant-a")[0].queried(), "the query marked it (AX-20)");
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn tenants_never_see_each_others_definitions_or_selections() {
    let (dir, engine) = open_engine("tenant-isolation");
    commit_users(&engine, "tenant-a", "op-1", &[5]);
    commit_users(&engine, "tenant-b", "op-2", &[6]);
    execute_dynamic(
        &engine,
        dynamic_spec("tenant-a", predicate(vec![equals("age", 5)]), Vec::new()),
        "tenant-a",
    )
    .expect("executes");
    execute_dynamic(
        &engine,
        dynamic_spec("tenant-b", predicate(vec![equals("age", 6)]), Vec::new()),
        "tenant-b",
    )
    .expect("executes");

    // Both tenants own exactly their own auto index and their own records.
    let a_definitions = definitions(&engine, "tenant-a");
    let b_definitions = definitions(&engine, "tenant-b");
    assert_eq!(a_definitions.len(), 1);
    assert_eq!(b_definitions.len(), 1);
    assert_eq!(a_definitions[0].tenant(), &tenant("tenant-a"));
    assert_eq!(b_definitions[0].tenant(), &tenant("tenant-b"));
    assert_eq!(a_definitions[0].name(), b_definitions[0].name());
    assert_eq!(records(&engine, "tenant-a").len(), 1);
    assert_eq!(records(&engine, "tenant-b").len(), 1);
    assert_eq!(records(&engine, "tenant-b")[0].tenant(), &tenant("tenant-b"));
    assert!(
        records(&engine, "tenant-b")[0].candidates().is_empty(),
        "tenant B started with no definitions: tenant A's index was never a candidate"
    );
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}
