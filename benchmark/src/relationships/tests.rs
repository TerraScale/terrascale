use std::{collections::BTreeMap, sync::Arc, thread};

use super::*;
use crate::types::{CollectionName, DocumentId, DocumentValue, TenantId};

fn scope(tenant: &str, database: &str) -> RelationshipScope {
    RelationshipScope::new(
        TenantId::try_new(tenant).expect("tenant"),
        DatabaseId::try_new(database).expect("database"),
    )
}

fn endpoint(collection: &str, document: &str) -> RelationshipEndpoint {
    RelationshipEndpoint::new(
        CollectionName::try_new(collection).expect("collection"),
        DocumentId::try_new(document).expect("document"),
    )
}

fn fixture() -> (RelationshipStore, RelationshipScope, RelationshipName) {
    let namespace = scope("tenant-a", "database-a");
    let relation = RelationshipName::try_new("member").expect("relation");
    let definition = RelationshipDefinition::new(
        relation.clone(),
        CollectionName::try_new("groups").expect("source collection"),
        CollectionName::try_new("users").expect("target collection"),
        RelationshipCardinality::ManyToMany,
    );
    (RelationshipStore::new(namespace.clone(), definition), namespace, relation)
}

fn edge(
    namespace: &RelationshipScope,
    relation: &RelationshipName,
    id: &str,
    group: &str,
    user: &str,
) -> EdgeRecord {
    EdgeRecord::try_new(
        namespace.clone(),
        relation.clone(),
        EdgeId::try_new(id).expect("edge ID"),
        endpoint("groups", group),
        endpoint("users", user),
    )
    .expect("edge")
}

#[test]
fn edge_identity_and_properties_are_typed_and_stable() {
    let (store, namespace, relation) = fixture();
    let mut properties = BTreeMap::new();
    properties.insert("role".to_owned(), DocumentValue::String("admin".to_owned()));
    let record = EdgeRecord::try_new_with_properties(
        namespace.clone(),
        relation.clone(),
        EdgeId::try_new("edge-1").expect("edge ID"),
        endpoint("groups", "g1"),
        endpoint("users", "u1"),
        properties,
    )
    .expect("record");
    let identity = record.identity();
    assert_eq!(identity.scope(), &namespace);
    assert_eq!(identity.relation(), &relation);
    assert_eq!(identity.edge_id().as_str(), "edge-1");
    assert!(matches!(store.add(record), Ok(AddOutcome::Inserted { generation: 1 })));
    let snapshot = store.snapshot().expect("snapshot");
    assert_eq!(snapshot.len(), 1);
    assert_eq!(
        snapshot.get(&EdgeId::try_new("edge-1").expect("ID")).unwrap().properties()["role"],
        DocumentValue::String("admin".to_owned())
    );
}

#[test]
fn scope_and_endpoint_collection_validation_fail_closed() {
    let (store, namespace, relation) = fixture();
    let foreign = edge(&scope("tenant-b", "database-a"), &relation, "e1", "g1", "u1");
    assert_eq!(store.add(foreign), Err(RelationshipError::ScopeMismatch));

    let wrong_collection = EdgeRecord::try_new(
        namespace,
        relation,
        EdgeId::try_new("e2").expect("ID"),
        endpoint("users", "g1"),
        endpoint("users", "u1"),
    )
    .expect("well-formed edge");
    assert_eq!(store.add(wrong_collection), Err(RelationshipError::EndpointCollectionMismatch));
}

#[test]
fn forward_and_reverse_indexes_are_deterministic_and_bounded() {
    let (store, namespace, relation) = fixture();
    let records = [
        edge(&namespace, &relation, "e2", "g1", "u2"),
        edge(&namespace, &relation, "e1", "g1", "u1"),
        edge(&namespace, &relation, "e3", "g2", "u1"),
    ];
    for record in records {
        store.add(record).expect("add");
    }
    let snapshot = store.snapshot().expect("snapshot");
    let bounds = TraversalBounds::new(1, 10, 4096);
    let forward = snapshot.forward(&endpoint("groups", "g1"), bounds).expect("forward");
    assert_eq!(
        forward.rows().iter().map(|row| row.edge_id().as_str()).collect::<Vec<_>>(),
        vec!["e1", "e2"]
    );
    let reverse = snapshot.reverse(&endpoint("users", "u1"), bounds).expect("reverse");
    assert_eq!(
        reverse.rows().iter().map(|row| row.endpoint().document().as_str()).collect::<Vec<_>>(),
        vec!["g1", "g2"]
    );
    assert!(forward.bytes() <= bounds.max_bytes);
}

#[test]
fn duplicate_adds_and_remove_are_idempotent_and_remove_wins_reordering() {
    let (store, namespace, relation) = fixture();
    let record = edge(&namespace, &relation, "e1", "g1", "u1");
    assert_eq!(
        store.add(record.clone()).expect("first add"),
        AddOutcome::Inserted { generation: 1 }
    );
    assert_eq!(
        store.add(record).expect("duplicate add"),
        AddOutcome::AlreadyPresent { generation: 1 }
    );
    let id = EdgeId::try_new("e1").expect("ID");
    assert_eq!(store.remove(&id).expect("remove"), RemoveOutcome::Removed { generation: 2 });
    assert_eq!(
        store.remove(&id).expect("duplicate remove"),
        RemoveOutcome::AlreadyRemoved { generation: 2 }
    );
    assert_eq!(
        store.add(edge(&namespace, &relation, "e1", "g1", "u1")).expect("late duplicate"),
        AddOutcome::Removed { generation: 2 }
    );
    assert!(store.snapshot().expect("snapshot").is_empty());
}

#[test]
fn conflicting_duplicate_identity_is_rejected() {
    let (store, namespace, relation) = fixture();
    store.add(edge(&namespace, &relation, "e1", "g1", "u1")).expect("first add");
    let conflict = edge(&namespace, &relation, "e1", "g2", "u1");
    assert_eq!(store.add(conflict), Err(RelationshipError::EdgeIdentityConflict));
}

#[test]
fn snapshots_are_immutable_and_generation_checked() {
    let (store, namespace, relation) = fixture();
    let old = store.snapshot().expect("empty snapshot");
    store.add(edge(&namespace, &relation, "e1", "g1", "u1")).expect("add");
    assert_eq!(old.generation(), 0);
    assert!(old.is_empty());
    assert_eq!(
        store.snapshot_at(0),
        Err(RelationshipError::StaleGeneration { expected: 0, actual: 1 })
    );
    let current = store.snapshot_at(1).expect("current snapshot");
    assert_eq!(current.len(), 1);
    assert_eq!(
        current.require_generation(2),
        Err(RelationshipError::StaleGeneration { expected: 2, actual: 1 })
    );
}

#[test]
fn traversal_refuses_result_and_byte_overflow_without_partial_rows() {
    let (store, namespace, relation) = fixture();
    store.add(edge(&namespace, &relation, "e1", "g1", "u1")).expect("add");
    store.add(edge(&namespace, &relation, "e2", "g1", "u2")).expect("add");
    let origin = endpoint("groups", "g1");
    assert_eq!(
        store.snapshot().expect("snapshot").forward(&origin, TraversalBounds::new(1, 1, 4096)),
        Err(RelationshipError::ResultLimitExceeded)
    );
    assert_eq!(
        store.snapshot().expect("snapshot").forward(&origin, TraversalBounds::new(1, 10, 1)),
        Err(RelationshipError::ByteLimitExceeded)
    );
    assert_eq!(
        store.snapshot().expect("snapshot").forward(&origin, TraversalBounds::new(0, 10, 4096)),
        Err(RelationshipError::InvalidTraversalBounds)
    );
}

#[test]
fn concurrent_reordered_duplicate_adds_publish_one_edge() {
    let (store, namespace, relation) = fixture();
    let store = Arc::new(store);
    let mut workers = Vec::new();
    for _ in 0..16 {
        let store = Arc::clone(&store);
        let namespace = namespace.clone();
        let relation = relation.clone();
        workers.push(thread::spawn(move || {
            store.add(edge(&namespace, &relation, "same", "g1", "u1")).expect("concurrent add")
        }));
    }
    let outcomes =
        workers.into_iter().map(|worker| worker.join().expect("worker")).collect::<Vec<_>>();
    assert_eq!(
        outcomes.iter().filter(|outcome| matches!(outcome, AddOutcome::Inserted { .. })).count(),
        1
    );
    assert_eq!(store.snapshot().expect("snapshot").len(), 1);
    assert_eq!(store.snapshot().expect("snapshot").generation(), 1);
}

#[test]
fn multi_hop_traversal_is_bounded_and_cycle_safe() {
    let namespace = scope("tenant-a", "database-a");
    let relation = RelationshipName::try_new("links").expect("relation");
    let definition = RelationshipDefinition::new(
        relation.clone(),
        CollectionName::try_new("nodes").expect("collection"),
        CollectionName::try_new("nodes").expect("collection"),
        RelationshipCardinality::ManyToMany,
    );
    let store = RelationshipStore::new(namespace.clone(), definition);
    for (id, source, target) in [("e1", "a", "b"), ("e2", "b", "c"), ("e3", "c", "a")] {
        store
            .add(
                EdgeRecord::try_new(
                    namespace.clone(),
                    relation.clone(),
                    EdgeId::try_new(id).expect("ID"),
                    endpoint("nodes", source),
                    endpoint("nodes", target),
                )
                .expect("edge"),
            )
            .expect("add");
    }
    let result = store
        .snapshot()
        .expect("snapshot")
        .forward(&endpoint("nodes", "a"), TraversalBounds::new(2, 10, 4096))
        .expect("bounded traversal");
    assert_eq!(result.rows().len(), 2);
    assert_eq!(result.rows()[0].hops(), 1);
    assert_eq!(result.rows()[1].hops(), 2);
}
