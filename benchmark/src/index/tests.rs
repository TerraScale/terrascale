//! In-crate index tests covering the W3-2 publication contract list.
//!
//! These tests drive the private index catalog and builder through the
//! Engine's publication-event sink and snapshot view. They prove the
//! publication rules only: nothing here claims an index-accelerated query
//! path, a persisted epoch, or a V1 index parity row.

use std::{
    collections::BTreeMap,
    path::PathBuf,
    sync::{
        Arc, Mutex,
        atomic::{AtomicU64, Ordering},
        mpsc,
    },
    time::Duration,
};

use super::{
    IndexBuilder, IndexCatalog, IndexDelta, IndexError, IndexKind, IndexLookupBounds,
    IndexLookupError, IndexSegment, MAX_GENERATION_HISTORY, RebuildOutcome,
};
use crate::{
    engine::{CommitOutcome, CommitPublicationSink, Engine, EngineConfig},
    operations::QueryInput,
    query::{
        IndexNamespace, QueryError, QueryOrdering, QueryPaging, QueryPredicate, QueryProjection,
        QuerySource, QuerySpec, StalenessMode, StalenessPolicy,
    },
    storage::{CommitPublicationEvent, StorageRootId},
    types::{
        Cancellation, CollectionName, Deadline, Document, DocumentId, DocumentValue,
        ExpectedVersion, LocalDurability, OperationId, StorageSequence, TenantId, TenantScope,
    },
};

fn location(tag: &str) -> PathBuf {
    static COUNTER: AtomicU64 = AtomicU64::new(0);
    let index = COUNTER.fetch_add(1, Ordering::Relaxed);
    std::env::temp_dir().join(format!("terrabase-w32-index-{tag}-{}-{index}", std::process::id()))
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

fn namespace(engine_tenant: &str, name: &str) -> crate::query::IndexNamespace {
    crate::query::IndexNamespace::try_new(tenant(engine_tenant), name).expect("valid namespace")
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

fn commit_one(engine: &Engine, tenant_name: &str, tag: &str, id: &str, age: i64) -> u64 {
    commit_documents(
        engine,
        tenant_name,
        tag,
        vec![object_document(id, "users", &[("age", DocumentValue::Integer(age))])],
    )
}

fn commit_documents(
    engine: &Engine,
    tenant_name: &str,
    tag: &str,
    documents: Vec<Document>,
) -> u64 {
    let tenant_scope = scope(engine, tenant_name);
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let operation = OperationId::try_new(tag).expect("valid identifier");
    let mut transaction = engine.begin(&tenant_scope, snapshot, operation).expect("begins");
    for document in documents {
        transaction.put(document, ExpectedVersion::Any).expect("stages");
    }
    match transaction.commit(LocalDurability::Synced) {
        CommitOutcome::Committed(receipt) => receipt.sequence().get(),
        other => panic!("commit did not succeed: {other:?}"),
    }
}

/// Forges one immutable publication event with a recorded root identity.
fn forged_event(sequence: u64) -> CommitPublicationEvent {
    CommitPublicationEvent::new(
        OperationId::try_new(&format!("op-{sequence}")).expect("valid identifier"),
        StorageRootId::from_recorded(format!("{sequence:032x}")),
        StorageSequence::new(sequence),
    )
}

fn published(outcome: Result<RebuildOutcome, IndexError>) -> (u64, u64, u64) {
    match outcome {
        Ok(RebuildOutcome::Published { epoch, generation, covered_sequence }) => {
            (epoch, generation, covered_sequence)
        }
        other => panic!("expected a publication, got {other:?}"),
    }
}

// ---------------------------------------------------------------------------
// The W3-2 red-test list
// ---------------------------------------------------------------------------

#[test]
fn index_delta_is_derived_from_the_immutable_commit_publication_event() {
    // Unit: the delta carries exactly the event's sequence and root.
    let event = forged_event(7);
    let delta = IndexDelta::from_event(&event);
    assert_eq!(delta.covered_sequence(), 7);
    assert_eq!(delta.root(), event.root().as_str());

    // Live: a committed write emits the event, and the catalog's delta is the
    // derivation of that exact immutable event, not an independent read.
    let (dir, engine) = open_engine("delta-from-event");
    let catalog = Arc::new(IndexCatalog::new());
    let events: Arc<Mutex<Vec<CommitPublicationEvent>>> = Arc::new(Mutex::new(Vec::new()));
    let recorded = Arc::clone(&events);
    let fed = Arc::clone(&catalog);
    let sink: CommitPublicationSink = Arc::new(move |event: &CommitPublicationEvent| {
        recorded.lock().expect("event lock").push(event.clone());
        fed.apply(IndexDelta::from_event(event));
    });
    let _sink_guard = engine.subscribe_publications(sink);
    let sequence = commit_one(&engine, "tenant-a", "op-1", "doc-1", 1);
    assert_eq!(sequence, 1);
    let recorded = events.lock().expect("event lock").clone();
    assert_eq!(recorded.len(), 1, "one commit publishes one event");
    assert_eq!(catalog.delta(), Some(IndexDelta::from_event(&recorded[0])));
    assert_eq!(catalog.delta().expect("observed").covered_sequence(), sequence);
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn index_publication_does_not_hold_the_storage_lock() {
    let (dir, engine) = open_engine("no-storage-lock");
    let engine = Arc::new(engine);
    let catalog = Arc::new(IndexCatalog::new());
    let tenant_scope = scope(&engine, "tenant-a");

    // While the publication event is processed, a storage read on another
    // thread must complete within a bounded wait: the event fires after
    // storage locks release, and the delta path takes only the index lock.
    let probe_result: Arc<Mutex<Option<bool>>> = Arc::new(Mutex::new(None));
    let fed = Arc::clone(&catalog);
    let probe_engine = Arc::clone(&engine);
    let probe_scope = tenant_scope.clone();
    let probe_outcome = Arc::clone(&probe_result);
    let _sink_guard =
        engine.subscribe_publications(Arc::new(move |event: &CommitPublicationEvent| {
            fed.apply(IndexDelta::from_event(event));
            let (sender, receiver) = mpsc::channel();
            let reader = Arc::clone(&probe_engine);
            let reader_scope = probe_scope.clone();
            std::thread::spawn(move || {
                let read = reader.read_snapshot(&reader_scope);
                let _ = sender.send(read.is_ok());
            });
            let observed = receiver.recv_timeout(Duration::from_secs(10));
            *probe_outcome.lock().expect("probe lock") = observed.ok();
        }));
    commit_one(&engine, "tenant-a", "op-1", "doc-1", 1);
    assert_eq!(
        *probe_result.lock().expect("probe lock"),
        Some(true),
        "storage must stay readable while a publication event is processed"
    );

    // Build and publish a segment: the read happens inside the Engine's
    // snapshot view with no index lock held, and the publish step holds only
    // the index lock. Storage state is untouched by index work.
    let index_namespace = namespace("tenant-a", "by-age");
    assert_eq!(catalog.ensure_namespace(&index_namespace, "age"), 1);
    let builder = IndexBuilder::new(&catalog, &engine);
    assert_eq!(published(builder.rebuild(&index_namespace, engine.identity())), (1, 1, 1));
    let before = engine.read_snapshot(&tenant_scope).expect("reads").sequence().get();
    assert_eq!(
        builder.rebuild(&index_namespace, engine.identity()),
        Ok(RebuildOutcome::RejectedAsStale {
            visible: Some(crate::query::ObservedGeneration::new(1, 1))
        }),
        "a rebuild that does not advance coverage is a replay, not a publication"
    );
    let after = engine.read_snapshot(&tenant_scope).expect("reads").sequence().get();
    assert_eq!(before, after, "index publication never advances storage");
    commit_one(&engine, "tenant-a", "op-2", "doc-2", 2);
    assert_eq!(
        published(builder.rebuild(&index_namespace, engine.identity())),
        (1, 2, 2),
        "a newer event publishes the next monotonic generation"
    );
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn index_generation_never_moves_backward_within_its_namespace_and_epoch() {
    let (dir, engine) = open_engine("monotonic-namespace");
    let catalog = Arc::new(IndexCatalog::new());
    let _sink_guard = engine.subscribe_publications(catalog.sink());
    commit_one(&engine, "tenant-a", "op-1", "doc-1", 1);
    let first = catalog.delta().expect("event");
    commit_one(&engine, "tenant-a", "op-2", "doc-2", 2);
    let second = catalog.delta().expect("event");
    commit_one(&engine, "tenant-a", "op-3", "doc-3", 3);
    let third = catalog.delta().expect("event");

    let index_namespace = namespace("tenant-a", "by-age");
    assert_eq!(catalog.ensure_namespace(&index_namespace, "age"), 1);
    let builder = IndexBuilder::new(&catalog, &engine);
    assert_eq!(
        published(builder.rebuild_from(&index_namespace, &first, engine.identity())),
        (1, 1, 1)
    );
    assert_eq!(
        published(builder.rebuild_from(&index_namespace, &second, engine.identity())),
        (1, 2, 2)
    );
    assert_eq!(
        published(builder.rebuild_from(&index_namespace, &third, engine.identity())),
        (1, 3, 3)
    );

    // Replayed and reordered builds never lower the visible generation.
    for replay in [&second, &first, &third] {
        assert_eq!(
            builder.rebuild_from(&index_namespace, replay, engine.identity()),
            Ok(RebuildOutcome::RejectedAsStale {
                visible: Some(crate::query::ObservedGeneration::new(1, 3))
            })
        );
    }
    assert_eq!(catalog.segment(&index_namespace).expect("published").generation(), 3);
    // The next real publication continues upward from the visible one.
    commit_one(&engine, "tenant-a", "op-4", "doc-4", 4);
    let fourth = catalog.delta().expect("event");
    assert_eq!(
        published(builder.rebuild_from(&index_namespace, &fourth, engine.identity())),
        (1, 4, 4)
    );
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn generation_history_is_bounded_and_old_observations_fail_closed() {
    let catalog = IndexCatalog::new();
    let namespace = namespace("tenant-a", "bounded-history");
    assert_eq!(catalog.ensure_namespace(&namespace, "age"), 1);

    for sequence in 1..=MAX_GENERATION_HISTORY + 1 {
        let segment = IndexSegment {
            namespace_key: namespace.catalog_key(),
            collection: None,
            epoch: 0,
            generation: 0,
            covered_sequence: sequence as u64,
            fields: vec!["age".to_owned()],
            entries: BTreeMap::new(),
        };
        assert!(matches!(
            catalog.publish(&namespace, segment),
            Ok(RebuildOutcome::Published { .. })
        ));
    }

    let state = catalog.state.lock().expect("index state");
    let history = &state
        .namespaces
        .get(&namespace.catalog_key())
        .expect("namespace")
        .epochs
        .get(&1)
        .expect("epoch")
        .history;
    assert_eq!(history.len(), MAX_GENERATION_HISTORY);
    assert_eq!(history.front().map(|(_, covered)| *covered), Some(2));
    drop(state);

    // A snapshot older than the retained suffix has no trustworthy
    // generation witness. The query staleness seam therefore reports no
    // observation instead of inventing one from a later publication.
    let view = &catalog as &dyn crate::query::GenerationView;
    assert_eq!(view.observed_generation(&namespace, StorageSequence::new(1)), None);
    assert_eq!(
        view.observed_generation(
            &namespace,
            StorageSequence::new((MAX_GENERATION_HISTORY + 1) as u64),
        ),
        Some(crate::query::ObservedGeneration::new(1, (MAX_GENERATION_HISTORY + 1) as u64))
    );
}

#[test]
fn index_rebuild_publishes_a_monotonic_generation() {
    let (dir, engine) = open_engine("rebuild-monotonic");
    let catalog = Arc::new(IndexCatalog::new());
    let _sink_guard = engine.subscribe_publications(catalog.sink());
    let index_namespace = namespace("tenant-a", "by-age");
    assert_eq!(catalog.ensure_namespace(&index_namespace, "age"), 1);
    let builder = IndexBuilder::new(&catalog, &engine);

    let mut generations = Vec::new();
    for index in 1..=4 {
        commit_one(&engine, "tenant-a", &format!("op-{index}"), &format!("doc-{index}"), index);
        let delta = catalog.delta().expect("event");
        let (epoch, generation, covered) =
            published(builder.rebuild_from(&index_namespace, &delta, engine.identity()));
        assert_eq!(epoch, 1, "no recreation happened, so the epoch persists");
        assert_eq!(covered, u64::try_from(index).expect("positive"));
        generations.push(generation);
    }
    let mut ordered = generations.clone();
    ordered.sort_unstable();
    ordered.dedup();
    assert_eq!(generations, ordered, "generations strictly increase and never repeat");
    assert_eq!(generations, [1, 2, 3, 4]);
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn replayed_index_build_cannot_overwrite_a_newer_generation() {
    let (dir, engine) = open_engine("replay-no-overwrite");
    let catalog = Arc::new(IndexCatalog::new());
    let _sink_guard = engine.subscribe_publications(catalog.sink());
    commit_one(&engine, "tenant-a", "op-1", "doc-1", 1);
    let first = catalog.delta().expect("event");
    commit_one(&engine, "tenant-a", "op-2", "doc-2", 2);
    let second = catalog.delta().expect("event");

    let index_namespace = namespace("tenant-a", "by-age");
    assert_eq!(catalog.ensure_namespace(&index_namespace, "age"), 1);
    let builder = IndexBuilder::new(&catalog, &engine);
    assert_eq!(
        published(builder.rebuild_from(&index_namespace, &first, engine.identity())),
        (1, 1, 1)
    );
    assert_eq!(
        published(builder.rebuild_from(&index_namespace, &second, engine.identity())),
        (1, 2, 2)
    );

    // A replayed older build is rejected and cannot overwrite the newer
    // segment: the visible generation, coverage, and entries all stay at the
    // newer publication.
    assert_eq!(
        builder.rebuild_from(&index_namespace, &first, engine.identity()),
        Ok(RebuildOutcome::RejectedAsStale {
            visible: Some(crate::query::ObservedGeneration::new(1, 2))
        })
    );
    let segment = catalog.segment(&index_namespace).expect("published");
    assert_eq!(segment.generation(), 2);
    assert_eq!(segment.covered_sequence(), 2);
    let indexed: usize = segment.entries().values().map(Vec::len).sum();
    assert_eq!(indexed, 2, "the segment still covers both documents");
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

// ---------------------------------------------------------------------------
// Focused extras
// ---------------------------------------------------------------------------

#[test]
fn catalog_ignores_replayed_or_reordered_events() {
    let catalog = IndexCatalog::new();
    assert!(catalog.apply(IndexDelta::from_event(&forged_event(2))));
    assert_eq!(catalog.delta().expect("observed").covered_sequence(), 2);
    assert!(
        !catalog.apply(IndexDelta::from_event(&forged_event(1))),
        "a reordered event is ignored"
    );
    assert!(
        !catalog.apply(IndexDelta::from_event(&forged_event(2))),
        "a replayed event is ignored"
    );
    assert_eq!(catalog.delta().expect("observed").covered_sequence(), 2);
    assert!(catalog.apply(IndexDelta::from_event(&forged_event(3))));
    assert_eq!(catalog.delta().expect("observed").covered_sequence(), 3);
}

#[test]
fn namespace_epochs_move_only_forward() {
    let catalog = IndexCatalog::new();
    let index_namespace = namespace("tenant-a", "by-age");
    assert_eq!(
        catalog.recreate_namespace(&index_namespace, 1, "age"),
        Err(IndexError::NamespaceUnknown)
    );
    assert_eq!(catalog.ensure_namespace(&index_namespace, "age"), 1);
    assert_eq!(catalog.ensure_namespace(&index_namespace, "age"), 1);
    assert_eq!(
        catalog.recreate_namespace(&index_namespace, 1, "age"),
        Err(IndexError::EpochNotNewer)
    );
    assert_eq!(catalog.recreate_namespace(&index_namespace, 2, "age"), Ok(()));
    assert_eq!(catalog.ensure_namespace(&index_namespace, "age"), 2);
    assert_eq!(
        catalog.recreate_namespace(&index_namespace, 2, "age"),
        Err(IndexError::EpochNotNewer)
    );
    assert_eq!(catalog.recreate_namespace(&index_namespace, 7, "age"), Ok(()));
}

#[test]
fn segments_index_the_registered_field_of_their_own_tenant() {
    let (dir, engine) = open_engine("segment-entries");
    let catalog = Arc::new(IndexCatalog::new());
    let _sink_guard = engine.subscribe_publications(catalog.sink());
    commit_one(&engine, "tenant-a", "op-1", "doc-a1", 1);
    commit_one(&engine, "tenant-a", "op-2", "doc-a2", 1);
    // Another tenant's documents share the storage sequence space but never
    // the namespace: the namespace identifies the tenant too.
    commit_one(&engine, "tenant-b", "op-3", "doc-b1", 1);

    let index_namespace = namespace("tenant-a", "by-age");
    assert_eq!(catalog.ensure_namespace(&index_namespace, "age"), 1);
    let builder = IndexBuilder::new(&catalog, &engine);
    let delta = catalog.delta().expect("event");
    assert_eq!(
        published(builder.rebuild_from(&index_namespace, &delta, engine.identity())),
        (1, 1, 3)
    );
    let segment = catalog.segment(&index_namespace).expect("published");
    // The catalog key joins the two parts with the unit separator, which
    // neither part may contain, so a tenant named like a prefix of another
    // tenant's key cannot alias this namespace's entry.
    assert_eq!(segment.namespace_key(), index_namespace.catalog_key());
    assert_ne!(segment.namespace_key(), "tenant-a/by-age");
    assert_eq!(segment.fields(), ["age".to_owned()]);
    let entry = segment.entries().get("integer:1").expect("indexed values");
    assert_eq!(entry.len(), 2);
    assert!(entry.contains(&DocumentId::try_new("doc-a1").expect("valid identifier")));
    assert!(!segment.entries().values().flatten().any(|id| id.as_str().starts_with("doc-b")));
    assert!(!segment.entries().contains_key("string:x"), "only the registered field is indexed");
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn definition_segments_are_isolated_to_their_collection() {
    let (dir, engine) = open_engine("collection-isolation");
    let catalog = engine.index_catalog();
    let namespace = namespace("tenant-a", "users-by-age");
    let definition = crate::query::optimizer::IndexDefinition::try_new(
        CollectionName::try_new("users").expect("valid identifier"),
        vec![
            crate::query::optimizer::IndexField::try_new(
                "age",
                crate::query::optimizer::IndexCapabilities::FULL,
            )
            .expect("valid field"),
        ],
        IndexKind::Explicit,
    )
    .expect("valid definition");
    catalog.register_explicit(&namespace, &definition).expect("registers");

    commit_documents(
        &engine,
        "tenant-a",
        "collection-isolation-op",
        vec![
            object_document("user-2", "users", &[("age", DocumentValue::Integer(7))]),
            object_document("audit-1", "audit", &[("age", DocumentValue::Integer(7))]),
            object_document("user-1", "users", &[("age", DocumentValue::Integer(7))]),
        ],
    );
    let builder = IndexBuilder::new(&catalog, &engine);
    let delta = catalog.delta().expect("event observed");
    assert!(matches!(
        builder.rebuild_from(&namespace, &delta, engine.identity()),
        Ok(RebuildOutcome::Published { .. })
    ));

    let segment = catalog.segment(&namespace).expect("published");
    assert_eq!(segment.collection(), Some(&CollectionName::try_new("users").expect("valid")));
    let posting = &segment.entries()["integer:7"];
    assert_eq!(posting.iter().map(DocumentId::as_str).collect::<Vec<_>>(), ["user-1", "user-2"]);
    assert!(!posting.iter().any(|id| id.as_str() == "audit-1"));
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn bounded_lookup_and_iteration_are_deterministic_and_refuse_overflow() {
    let (dir, engine) = open_engine("bounded-reads");
    let catalog = engine.index_catalog();
    let namespace = namespace("tenant-a", "users-by-age");
    let definition = crate::query::optimizer::IndexDefinition::try_new(
        CollectionName::try_new("users").expect("valid identifier"),
        vec![
            crate::query::optimizer::IndexField::try_new(
                "age",
                crate::query::optimizer::IndexCapabilities::FULL,
            )
            .expect("valid field"),
        ],
        IndexKind::Explicit,
    )
    .expect("valid definition");
    catalog.register_explicit(&namespace, &definition).expect("registers");
    commit_documents(
        &engine,
        "tenant-a",
        "bounded-reads-op",
        vec![
            object_document("z-user", "users", &[("age", DocumentValue::Integer(8))]),
            object_document("a-user", "users", &[("age", DocumentValue::Integer(7))]),
            object_document("m-user", "users", &[("age", DocumentValue::Integer(8))]),
        ],
    );
    let builder = IndexBuilder::new(&catalog, &engine);
    let delta = catalog.delta().expect("event observed");
    builder.rebuild_from(&namespace, &delta, engine.identity()).expect("publishes");
    let segment = catalog.segment(&namespace).expect("published");

    let key = "integer:8";
    let key_bytes = key.len() + "m-user".len() + key.len() + "z-user".len();
    let lookup = segment
        .lookup(key, IndexLookupBounds::new(2, key_bytes))
        .expect("exact bounds permit complete lookup");
    assert_eq!(lookup.bytes(), key_bytes);
    assert_eq!(lookup.identity(), segment.identity());
    assert_eq!(
        lookup.rows().iter().map(|row| (row.key(), row.document_id().as_str())).collect::<Vec<_>>(),
        [(key, "m-user"), (key, "z-user")]
    );
    assert_eq!(
        segment.lookup(key, IndexLookupBounds::new(1, key_bytes)),
        Err(IndexLookupError::RowLimitExceeded { limit: 1, required: 2 })
    );
    assert_eq!(
        segment.lookup(key, IndexLookupBounds::new(2, key_bytes - 1)),
        Err(IndexLookupError::ByteLimitExceeded { limit: key_bytes - 1, required: key_bytes })
    );

    let all_rows = segment
        .iter_bounded(IndexLookupBounds::new(3, key_bytes + key.len() + "a-user".len()))
        .expect("exact full-segment bounds permit complete iteration")
        .map(|(key, id)| (key.to_owned(), id.as_str().to_owned()))
        .collect::<Vec<_>>();
    assert_eq!(
        all_rows,
        [
            ("integer:7".to_owned(), "a-user".to_owned()),
            ("integer:8".to_owned(), "m-user".to_owned()),
            ("integer:8".to_owned(), "z-user".to_owned())
        ]
    );
    let iterator = segment
        .iter_bounded(IndexLookupBounds::new(3, key_bytes + key.len() + "a-user".len()))
        .expect("bounds permit iterator");
    assert_eq!(iterator.identity(), segment.identity());
    assert!(matches!(
        segment.iter_bounded(IndexLookupBounds::new(2, usize::MAX)),
        Err(IndexLookupError::RowLimitExceeded { limit: 2, required: 3 })
    ));
    assert!(matches!(
        segment.iter_bounded(IndexLookupBounds::new(
            usize::MAX,
            key_bytes + key.len() + "a-user".len() - 1
        )),
        Err(IndexLookupError::ByteLimitExceeded { .. })
    ));
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn bounded_reads_keep_empty_and_missing_values_unindexed() {
    let (dir, engine) = open_engine("bounded-empty-values");
    let catalog = engine.index_catalog();
    let namespace = namespace("tenant-a", "users-by-age");
    let definition = crate::query::optimizer::IndexDefinition::try_new(
        CollectionName::try_new("users").expect("valid identifier"),
        vec![
            crate::query::optimizer::IndexField::try_new(
                "age",
                crate::query::optimizer::IndexCapabilities::FULL,
            )
            .expect("valid field"),
        ],
        IndexKind::Explicit,
    )
    .expect("valid definition");
    catalog.register_explicit(&namespace, &definition).expect("registers");
    commit_documents(
        &engine,
        "tenant-a",
        "bounded-empty-values-op",
        vec![
            object_document("good", "users", &[("age", DocumentValue::Integer(1))]),
            object_document("missing", "users", &[("name", DocumentValue::String("n".into()))]),
            object_document("array", "users", &[("age", DocumentValue::Array(Vec::new()))]),
            object_document("object", "users", &[("age", DocumentValue::Object(BTreeMap::new()))]),
        ],
    );
    let builder = IndexBuilder::new(&catalog, &engine);
    let delta = catalog.delta().expect("event observed");
    builder.rebuild_from(&namespace, &delta, engine.identity()).expect("publishes");
    let segment = catalog.segment(&namespace).expect("published");
    assert_eq!(segment.entries().len(), 1);
    assert_eq!(
        segment.lookup("missing", IndexLookupBounds::new(0, 0)).expect("empty lookup").rows(),
        []
    );
    assert_eq!(
        segment.lookup("array", IndexLookupBounds::new(0, 0)).expect("empty lookup").rows(),
        []
    );
    assert_eq!(segment.identity().epoch(), 1);
    assert_eq!(segment.identity().generation(), 1);
    assert_eq!(segment.identity().covered_sequence(), delta.covered_sequence());
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn definition_namespaces_build_composite_segment_keys() {
    // A definition-carrying namespace indexes every definition field: the
    // segment key composes the per-field canonical keys with the unit
    // separator, and a document missing any indexed field has no entry.
    let (dir, engine) = open_engine("composite-segments");
    let catalog = engine.index_catalog();
    commit_one(&engine, "tenant-a", "op-1", "doc-1", 1);

    let definition = crate::query::optimizer::IndexDefinition::try_new(
        CollectionName::try_new("users").expect("valid identifier"),
        vec![
            crate::query::optimizer::IndexField::try_new(
                "age",
                crate::query::optimizer::IndexCapabilities::FULL,
            )
            .expect("valid field"),
            crate::query::optimizer::IndexField::try_new(
                "name",
                crate::query::optimizer::IndexCapabilities::FULL,
            )
            .expect("valid field"),
        ],
        crate::query::optimizer::IndexKind::Auto,
    )
    .expect("valid definition");
    let registration = catalog
        .register_auto(&tenant("tenant-a"), &definition)
        .expect("registers under the deterministic name");
    assert!(registration.created());
    assert_eq!(registration.namespace().name(), "Auto/users/ByAgeAndName");

    // A document with both fields, one with only age, and one with neither:
    // only the complete tuple is indexed.
    let tenant_scope = scope(&engine, "tenant-a");
    let snapshot = engine.read_snapshot(&tenant_scope).expect("snapshot");
    let operation = OperationId::try_new("op-2").expect("valid identifier");
    let mut transaction = engine.begin(&tenant_scope, snapshot, operation).expect("begins");
    transaction
        .put(
            object_document(
                "doc-2",
                "users",
                &[("age", DocumentValue::Integer(2)), ("name", DocumentValue::String("k".into()))],
            ),
            ExpectedVersion::Any,
        )
        .expect("stages");
    transaction
        .put(
            object_document("doc-3", "users", &[("rank", DocumentValue::Integer(9))]),
            ExpectedVersion::Any,
        )
        .expect("stages");
    match transaction.commit(LocalDurability::Synced) {
        CommitOutcome::Committed(_) => {}
        other => panic!("commit did not succeed: {other:?}"),
    }

    let builder = IndexBuilder::new(&catalog, &engine);
    let delta = catalog.delta().expect("event");
    assert!(matches!(
        builder.rebuild_from(registration.namespace(), &delta, engine.identity()),
        Ok(RebuildOutcome::Published { .. })
    ));
    let segment = catalog.segment(registration.namespace()).expect("published");
    assert_eq!(
        segment.fields(),
        ["age".to_owned(), "name".to_owned()],
        "the segment carries the definition's field order"
    );
    // doc-1 and doc-2 carry age but only doc-2 carries name; doc-3 carries
    // neither; only the complete tuple forms an entry.
    let composite =
        segment.entries().keys().map(std::string::ToString::to_string).collect::<Vec<_>>();
    assert_eq!(
        composite,
        [format!("integer:2\u{1f}{}", "string:k")],
        "only complete tuples are indexed; missing fields form no entry"
    );
    let indexed = &segment.entries()[&format!("integer:2\u{1f}{}", "string:k")];
    assert_eq!(indexed.len(), 1);
    assert_eq!(indexed[0].as_str(), "doc-2");
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn builder_fails_typed_before_any_namespace_or_event_exists() {
    let (dir, engine) = open_engine("builder-typed-failures");
    let catalog = Arc::new(IndexCatalog::new());
    let builder = IndexBuilder::new(&catalog, &engine);
    let index_namespace = namespace("tenant-a", "by-age");
    assert_eq!(
        builder.rebuild(&index_namespace, engine.identity()),
        Err(IndexError::NoPublicationObserved)
    );
    let _sink_guard = engine.subscribe_publications(catalog.sink());
    commit_one(&engine, "tenant-a", "op-1", "doc-1", 1);
    assert_eq!(
        builder.rebuild(&index_namespace, engine.identity()),
        Err(IndexError::NamespaceUnknown),
        "an unregistered namespace never builds"
    );
    // A namespace of another engine identity cannot read the snapshot view.
    assert_eq!(
        builder.rebuild_from(
            &index_namespace,
            &catalog.delta().expect("event"),
            engine.identity() ^ 1
        ),
        Err(IndexError::StorageFailure)
    );
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

#[test]
fn index_lock_holders_never_block_storage_reads() {
    // The lock order is one-way: storage access never waits for the index
    // lock, because publish is the only step that holds it and it touches no
    // Engine state.
    let (dir, engine) = open_engine("lock-order");
    let catalog = IndexCatalog::new();
    let tenant_scope = scope(&engine, "tenant-a");
    commit_one(&engine, "tenant-a", "op-1", "doc-1", 1);
    let read = catalog.with_state_lock_held(|| {
        engine.read_snapshot(&tenant_scope).expect("a storage read does not need the index lock")
    });
    let _ = read;
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}

/// Builds a strict typed specification for one tenant claiming one required
/// index namespace, the smallest query that can hit a staleness gate.
fn strict_spec(claim: &str, required: IndexNamespace) -> QuerySpec {
    QuerySpec::try_new(
        TenantId::try_new(claim).expect("valid tenant"),
        QuerySource::try_new(CollectionName::try_new("users").expect("valid identifier"))
            .expect("user collection"),
        QueryPredicate::all(Vec::new()).expect("valid conjunction"),
        QueryOrdering::default(),
        QueryProjection::whole_document(),
        QueryPaging::try_new(0, None).expect("valid paging"),
        StalenessPolicy::try_new(StalenessMode::Strict, vec![required]).expect("valid policy"),
        Deadline::after(Duration::from_secs(30)).expect("valid deadline"),
        Cancellation::new(),
    )
    .expect("valid specification")
}

#[test]
fn tenant_and_name_separator_cannot_alias_a_namespace() {
    let (dir, engine) = open_engine("namespace-aliasing");
    // The Engine-owned catalog (W4-CA-3) is fed by its own registered sink,
    // so the in-crate tests drive it directly instead of installing one.
    let catalog = engine.index_catalog();
    // Both tenants commit, so both namespaces can build from real events.
    commit_one(&engine, "a", "op-1", "doc-a1", 1);
    commit_one(&engine, "a/b", "op-2", "doc-b1", 1);

    let aliased_left = IndexNamespace::try_new(tenant("a"), "b/c").expect("valid namespace");
    let aliased_right = IndexNamespace::try_new(tenant("a/b"), "c").expect("valid namespace");
    // A plain '/' join collapses these two namespaces into one catalog key,
    // because both validators admit '/' inside tenant and name text. The
    // real key joins with a byte neither validator admits, so the pair stays
    // injective and a namespace keeps identifying one tenant and one
    // definition.
    assert_ne!(aliased_left.catalog_key(), aliased_right.catalog_key());
    assert_eq!(aliased_left.catalog_key(), "a\u{1f}b/c");

    assert_eq!(catalog.ensure_namespace(&aliased_left, "age"), 1);
    assert_eq!(catalog.ensure_namespace(&aliased_right, "age"), 1);
    let builder = IndexBuilder::new(&catalog, &engine);
    // Only the right namespace publishes.
    let delta = catalog.delta().expect("event observed");
    assert!(matches!(
        builder.rebuild_from(&aliased_right, &delta, engine.identity()),
        Ok(RebuildOutcome::Published { generation: 1, .. })
    ));
    // The two namespaces are independent entries: the left one has no
    // segment of its own.
    assert!(catalog.segment(&aliased_left).is_none());
    assert!(catalog.segment(&aliased_right).is_some());

    // Neither can satisfy the other's staleness gate. A strict query for
    // tenant "a" requiring ("a", "b/c") must fail with IndexNotReady even
    // though ("a/b", "c") has a fresh published generation: under a '/'
    // join, that alias would have opened this gate.
    let store = engine.queries();
    let scope_left = TenantScope::from_parts(tenant("a"), engine.identity());
    let snapshot = engine.read_snapshot(&scope_left).expect("snapshot");
    let plan =
        store.compile(QueryInput::typed(strict_spec("a", aliased_left.clone()))).expect("compiles");
    assert_eq!(
        store.execute(&plan, &snapshot).unwrap_err(),
        QueryError::IndexNotReady,
        "another namespace's generation must never open this gate"
    );

    // Once the left namespace publishes its own generation, its gate opens,
    // and the two generation sequences advance independently.
    assert!(matches!(
        builder.rebuild(&aliased_left, engine.identity()),
        Ok(RebuildOutcome::Published { generation: 1, .. })
    ));
    commit_one(&engine, "a", "op-3", "doc-a2", 2);
    assert!(matches!(
        builder.rebuild(&aliased_left, engine.identity()),
        Ok(RebuildOutcome::Published { generation: 2, .. })
    ));
    let fresh = engine.read_snapshot(&scope_left).expect("snapshot");
    assert!(store.execute(&plan, &fresh).is_ok(), "its own generation opens its gate");
    assert_eq!(catalog.segment(&aliased_left).expect("published").generation(), 2);
    assert_eq!(catalog.segment(&aliased_right).expect("published").generation(), 1);
    drop(engine);
    std::fs::remove_dir_all(&dir).ok();
}
