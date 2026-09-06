use serde_json::json;

use crate::*;
use openraft::storage::RaftStateMachine;
use openraft::{CommittedLeaderId, Entry, EntryPayload, LogId};

fn command(key: &str, action: Action) -> Command {
    Command { idempotency_key: key.into(), observed_at_ms: 1, action }
}

fn record(value: serde_json::Value) -> Record {
    serde_json::from_value(value).unwrap()
}

async fn apply(store: &mut SqliteStore, index: u64, key: &str, action: Action) -> Outcome {
    store
        .apply([Entry {
            log_id: LogId::new(CommittedLeaderId::new(1, 1), index),
            payload: EntryPayload::Normal(command(key, action)),
        }])
        .await
        .unwrap()
        .remove(0)
}

fn join(
    model: &str,
    output_field: &str,
    from_field: &str,
    to_field: &str,
    relation: JoinRelation,
) -> QueryJoin {
    QueryJoin {
        model: model.into(),
        output_field: output_field.into(),
        from_field: from_field.into(),
        to_field: to_field.into(),
        relation,
        limit: 100,
    }
}

#[tokio::test]
async fn explicit_joins_are_bounded_projected_and_snapshot_consistent() {
    let dir = tempfile::tempdir().unwrap();
    let mut store = SqliteStore::open(&dir.path().join("auth.sqlite"), "joins").unwrap();
    let mut index = 1;
    for (model, value) in [
        ("user", json!({"id":"u1","profile_id":"p1","score":2})),
        ("user", json!({"id":"u2","score":1})),
        ("profile", json!({"id":"p1","name":"Ada"})),
        ("post", json!({"id":"post-1","user_id":"u1","title":"first"})),
        ("post", json!({"id":"post-2","user_id":"u1","title":"second"})),
    ] {
        assert!(matches!(
            apply(
                &mut store,
                index,
                &format!("insert-{index}"),
                Action::InsertUnique { model: model.into(), record: record(value) }
            )
            .await,
            Outcome::Record { .. }
        ));
        index += 1;
    }
    let output = apply(
        &mut store,
        index,
        "find",
        Action::Find {
            model: "user".into(),
            predicates: vec![],
            offset: 0,
            limit: 10,
            select: Some(vec!["id".into()]),
            sort: None,
            joins: vec![
                join("profile", "profile", "profile_id", "id", JoinRelation::OneToOne),
                join("post", "posts", "id", "user_id", JoinRelation::OneToMany),
            ],
        },
    )
    .await;
    let Outcome::Records { records } = output else { panic!("expected records") };
    assert_eq!(records[0]["id"], "u1");
    assert_eq!(records[0]["profile"]["name"], "Ada");
    assert_eq!(records[0]["posts"].as_array().unwrap().len(), 2);
    assert!(records[0].get("score").is_none());
    assert_eq!(records[1]["profile"], serde_json::Value::Null);
    assert_eq!(records[1]["posts"], json!([]));

    let collision = apply(
        &mut store,
        index + 1,
        "collision",
        Action::Find {
            model: "user".into(),
            predicates: vec![],
            offset: 0,
            limit: 1,
            select: None,
            sort: None,
            joins: vec![join("profile", "id", "profile_id", "id", JoinRelation::OneToOne)],
        },
    )
    .await;
    assert!(matches!(collision, Outcome::Invalid { .. }));
}

#[tokio::test]
async fn find_sort_ties_direction_offset_and_zero_limit_are_deterministic() {
    let dir = tempfile::tempdir().unwrap();
    let mut store = SqliteStore::open(&dir.path().join("auth.sqlite"), "sorts").unwrap();
    for (index, id) in ["a", "b", "c"].into_iter().enumerate() {
        assert!(matches!(
            apply(
                &mut store,
                index as u64 + 1,
                id,
                Action::InsertUnique {
                    model: "item".into(),
                    record: record(json!({"id":id,"score":1})),
                }
            )
            .await,
            Outcome::Record { .. }
        ));
    }
    let output = apply(
        &mut store,
        4,
        "desc",
        Action::Find {
            model: "item".into(),
            predicates: vec![],
            offset: 1,
            limit: 1,
            select: None,
            sort: Some(QuerySort { field: "score".into(), direction: SortDirection::Desc }),
            joins: vec![],
        },
    )
    .await;
    let Outcome::Records { records } = output else { panic!("expected records") };
    assert_eq!(records[0]["id"], "b");
    let zero = apply(
        &mut store,
        5,
        "zero",
        Action::Find {
            model: "item".into(),
            predicates: vec![],
            offset: 1000,
            limit: 0,
            select: None,
            sort: None,
            joins: vec![],
        },
    )
    .await;
    assert_eq!(zero, Outcome::Records { records: vec![] });
}

#[tokio::test]
async fn joined_response_over_one_megabyte_is_rejected_atomically() {
    let dir = tempfile::tempdir().unwrap();
    let mut store = SqliteStore::open(&dir.path().join("auth.sqlite"), "join-bytes").unwrap();
    let payload = "x".repeat(200_000);
    let mut index = 1;
    for child_index in 0..6_u64 {
        assert!(matches!(
            apply(
                &mut store,
                index,
                &format!("base-{child_index}"),
                Action::InsertUnique {
                    model: "base".into(),
                    record: record(json!({"id":format!("b{child_index}"),"key":"k"})),
                }
            )
            .await,
            Outcome::Record { .. }
        ));
        index += 1;
        assert!(matches!(
            apply(
                &mut store,
                index,
                &format!("child-{child_index}"),
                Action::InsertUnique {
                    model: "child".into(),
                    record: record(
                        json!({"id":format!("c{child_index}"),"key":"k","payload":payload})
                    ),
                }
            )
            .await,
            Outcome::Record { .. }
        ));
        index += 1;
    }
    let result = apply(
        &mut store,
        30,
        "oversize",
        Action::Find {
            model: "base".into(),
            predicates: vec![],
            offset: 0,
            limit: 6,
            select: None,
            sort: None,
            joins: vec![join("child", "children", "key", "key", JoinRelation::OneToMany)],
        },
    )
    .await;
    assert!(matches!(result, Outcome::Invalid { .. }));
}

#[tokio::test]
async fn joins_enforce_duplicate_outputs_one_to_one_and_retained_bytes() {
    let dir = tempfile::tempdir().unwrap();
    let mut store = SqliteStore::open(&dir.path().join("auth.sqlite"), "join-bounds").unwrap();
    let mut index = 1;
    for id in ["a", "b"] {
        assert!(matches!(
            apply(
                &mut store,
                index,
                id,
                Action::InsertUnique {
                    model: "base".into(),
                    record: record(json!({"id":id,"key":"k","sort":1})),
                }
            )
            .await,
            Outcome::Record { .. }
        ));
        index += 1;
    }
    for id in ["first", "second"] {
        assert!(matches!(
            apply(
                &mut store,
                index,
                id,
                Action::InsertUnique {
                    model: "related".into(),
                    record: record(json!({"id":id,"key":"k"})),
                }
            )
            .await,
            Outcome::Record { .. }
        ));
        index += 1;
    }
    let one = apply(
        &mut store,
        index,
        "one",
        Action::Find {
            model: "base".into(),
            predicates: vec![],
            offset: 0,
            limit: 1,
            select: None,
            sort: None,
            joins: vec![join("related", "related", "key", "key", JoinRelation::OneToOne)],
        },
    )
    .await;
    let Outcome::Records { records } = one else { panic!("expected records") };
    assert_eq!(records[0]["related"]["id"], "first");
    let duplicate = apply(
        &mut store,
        index + 1,
        "duplicate",
        Action::Find {
            model: "base".into(),
            predicates: vec![],
            offset: 0,
            limit: 1,
            select: None,
            sort: None,
            joins: vec![
                join("related", "same", "key", "key", JoinRelation::OneToOne),
                join("related", "same", "key", "key", JoinRelation::OneToMany),
            ],
        },
    )
    .await;
    assert!(matches!(duplicate, Outcome::Invalid { .. }));

    let large = "x".repeat(600_000);
    for n in 0..8 {
        assert!(matches!(
            apply(
                &mut store,
                index + 2 + n,
                &format!("large-{n}"),
                Action::InsertUnique {
                    model: "large".into(),
                    record: record(json!({"id":format!("large-{n}"),"sort":n,"body":large})),
                }
            )
            .await,
            Outcome::Record { .. }
        ));
    }
    let retained = apply(
        &mut store,
        index + 20,
        "retained",
        Action::Find {
            model: "large".into(),
            predicates: vec![],
            offset: 0,
            limit: 8,
            select: Some(vec!["id".into()]),
            sort: Some(QuerySort { field: "sort".into(), direction: SortDirection::Asc }),
            joins: vec![],
        },
    )
    .await;
    assert!(matches!(retained, Outcome::Invalid { .. }));
}
