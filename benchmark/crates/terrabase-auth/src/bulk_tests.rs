use super::*;
use openraft::storage::RaftStateMachine;
use openraft::{CommittedLeaderId, Entry, EntryPayload, LogId};
use serde_json::json;
use std::collections::BTreeMap;

fn command(key: &str, action: Action) -> Command {
    Command { idempotency_key: key.into(), observed_at_ms: 1_000, action }
}

fn record(id: &str, version: u64) -> Record {
    serde_json::from_value(json!({"id": id, "version": version})).unwrap()
}

fn apply_entry(store: &mut SqliteStore, index: u64, command: Command) -> Outcome {
    tokio::runtime::Runtime::new().unwrap().block_on(async {
        store
            .apply([Entry {
                log_id: LogId::new(CommittedLeaderId::new(1, 1), index),
                payload: EntryPayload::Normal(command),
            }])
            .await
            .unwrap()
            .remove(0)
    })
}

fn predicate(field: &str, value: serde_json::Value) -> Predicate {
    serde_json::from_value(json!({"field": field, "value": value})).unwrap()
}

fn transaction(id: &str, action: Action) -> Action {
    Action::TransactionOperation { transaction_id: id.into(), action: Box::new(action) }
}

fn open_store(dir: &tempfile::TempDir) -> SqliteStore {
    SqliteStore::open(&dir.path().join("auth.sqlite"), "group").unwrap()
}

#[test]
fn bulk_row_and_byte_bounds_are_invalid_and_atomic() {
    let dir = tempfile::tempdir().unwrap();
    let mut store = open_store(&dir);
    let entries = (0..1001)
        .map(|index| Entry {
            log_id: LogId::new(CommittedLeaderId::new(1, 1), index + 1),
            payload: EntryPayload::Normal(command(
                &format!("row-{index}"),
                Action::InsertUnique {
                    model: "user".into(),
                    record: record(&index.to_string(), 1),
                },
            )),
        })
        .collect::<Vec<_>>();
    tokio::runtime::Runtime::new().unwrap().block_on(async { store.apply(entries).await.unwrap() });
    assert!(matches!(
        apply_entry(
            &mut store,
            1002,
            command(
                "row-bound",
                Action::UpdateMany {
                    model: "user".into(),
                    predicates: vec![],
                    update: serde_json::from_value(json!({"version": 2})).unwrap(),
                },
            ),
        ),
        Outcome::Invalid { .. }
    ));
    assert_eq!(store.read_record("user", "0").unwrap().unwrap()["version"], 1);
    assert_eq!(store.read_record("user", "1000").unwrap().unwrap()["version"], 1);

    let dir = tempfile::tempdir().unwrap();
    let mut store = open_store(&dir);
    for index in 0..20 {
        let mut value = record(&index.to_string(), 1);
        value.insert("payload".into(), json!("x".repeat(60_000)));
        assert!(matches!(
            apply_entry(
                &mut store,
                index + 1,
                command(
                    &format!("bytes-{index}"),
                    Action::InsertUnique { model: "user".into(), record: value }
                ),
            ),
            Outcome::Record { .. }
        ));
    }
    assert!(matches!(
        apply_entry(
            &mut store,
            21,
            command(
                "byte-bound",
                Action::UpdateMany {
                    model: "user".into(),
                    predicates: vec![],
                    update: serde_json::from_value(json!({"version": 2})).unwrap(),
                },
            ),
        ),
        Outcome::Invalid { .. }
    ));
    assert_eq!(store.read_record("user", "0").unwrap().unwrap()["version"], 1);
}

#[test]
fn bounded_failure_inside_transaction_poisons_without_partial_commit() {
    let dir = tempfile::tempdir().unwrap();
    let mut store = open_store(&dir);
    for index in 0..1001 {
        assert!(matches!(
            apply_entry(
                &mut store,
                index + 1,
                command(
                    &format!("insert-{index}"),
                    Action::InsertUnique {
                        model: "user".into(),
                        record: record(&index.to_string(), 1),
                    }
                ),
            ),
            Outcome::Record { .. }
        ));
    }
    assert!(matches!(
        apply_entry(
            &mut store,
            1002,
            command("begin", Action::BeginTransaction { transaction_id: "bounded".into() }),
        ),
        Outcome::Transaction { transaction_id, .. } if transaction_id == "bounded#1"
    ));
    assert!(matches!(
        apply_entry(
            &mut store,
            1003,
            command(
                "bounded-op",
                transaction(
                    "bounded#1",
                    Action::UpdateMany {
                        model: "user".into(),
                        predicates: vec![],
                        update: serde_json::from_value(json!({"version": 2})).unwrap(),
                    }
                ),
            ),
        ),
        Outcome::Invalid { .. }
    ));
    assert_eq!(
        apply_entry(
            &mut store,
            1004,
            command("commit", Action::CommitTransaction { transaction_id: "bounded#1".into() }),
        ),
        Outcome::TransactionConflict
    );
    assert_eq!(store.read_record("user", "0").unwrap().unwrap()["version"], 1);
}

#[test]
fn transaction_bulk_update_commits_delete_rolls_back_and_count_does_not_advance_revision() {
    let dir = tempfile::tempdir().unwrap();
    let mut store = open_store(&dir);
    for index in 0..3 {
        assert!(matches!(
            apply_entry(
                &mut store,
                index + 1,
                command(
                    &format!("insert-{index}"),
                    Action::InsertUnique {
                        model: "user".into(),
                        record: record(&index.to_string(), 1),
                    }
                ),
            ),
            Outcome::Record { .. }
        ));
    }
    assert!(matches!(
        apply_entry(
            &mut store,
            4,
            command("begin", Action::BeginTransaction { transaction_id: "bulk".into() }),
        ),
        Outcome::Transaction { transaction_id, .. } if transaction_id == "bulk#1"
    ));
    assert_eq!(
        apply_entry(
            &mut store,
            5,
            command(
                "update",
                transaction(
                    "bulk#1",
                    Action::UpdateMany {
                        model: "user".into(),
                        predicates: vec![predicate("version", json!(1))],
                        update: serde_json::from_value(json!({"version": 2})).unwrap(),
                    }
                ),
            ),
        ),
        Outcome::Count { count: 3 }
    );
    assert_eq!(
        apply_entry(
            &mut store,
            6,
            command("commit", Action::CommitTransaction { transaction_id: "bulk#1".into() }),
        ),
        Outcome::Applied
    );
    assert_eq!(store.read_record("user", "0").unwrap().unwrap()["version"], 2);

    assert!(matches!(
        apply_entry(
            &mut store,
            7,
            command("begin-delete", Action::BeginTransaction { transaction_id: "delete".into() }),
        ),
        Outcome::Transaction { transaction_id, .. } if transaction_id == "delete#2"
    ));
    assert_eq!(
        apply_entry(
            &mut store,
            8,
            command(
                "delete",
                transaction(
                    "delete#2",
                    Action::DeleteMany { model: "user".into(), predicates: vec![] }
                ),
            ),
        ),
        Outcome::Count { count: 3 }
    );
    assert_eq!(
        apply_entry(
            &mut store,
            9,
            command("rollback", Action::RollbackTransaction { transaction_id: "delete#2".into() }),
        ),
        Outcome::Applied
    );
    assert!(store.read_record("user", "0").unwrap().is_some());

    assert!(matches!(
        apply_entry(
            &mut store,
            10,
            command("begin-count", Action::BeginTransaction { transaction_id: "count".into() }),
        ),
        Outcome::Transaction { transaction_id, .. } if transaction_id == "count#3"
    ));
    let count = apply_entry(
        &mut store,
        11,
        command("count", Action::Count { model: "user".into(), predicates: vec![] }),
    );
    assert_eq!(count, Outcome::Count { count: 3 });
    assert_eq!(
        apply_entry(
            &mut store,
            12,
            command(
                "count-update",
                transaction(
                    "count#3",
                    Action::UpdateMany {
                        model: "user".into(),
                        predicates: vec![],
                        update: serde_json::from_value(json!({"version": 3})).unwrap(),
                    },
                ),
            ),
        ),
        Outcome::Count { count: 3 }
    );
    assert_eq!(
        apply_entry(
            &mut store,
            13,
            command("count-commit", Action::CommitTransaction { transaction_id: "count#3".into() }),
        ),
        Outcome::Applied
    );
    assert_eq!(store.read_record("user", "0").unwrap().unwrap()["version"], 3);
}

#[test]
fn increment_preserves_signed_and_unsigned_integer_exactness_and_rejects_overflow() {
    let dir = tempfile::tempdir().unwrap();
    let mut store = open_store(&dir);
    let mut negative = record("negative", 0);
    negative.insert("value".into(), json!(-5));
    let mut minimum = record("minimum", 0);
    minimum.insert("value".into(), serde_json::Value::Number(serde_json::Number::from(i64::MIN)));
    let mut maximum = record("maximum", 0);
    maximum.insert("value".into(), serde_json::Value::Number(serde_json::Number::from(u64::MAX)));
    let mut exact = record("exact", 0);
    exact.insert(
        "value".into(),
        serde_json::Value::Number(serde_json::Number::from(9_007_199_254_740_993u64)),
    );
    for (index, value) in [negative, minimum, maximum, exact].into_iter().enumerate() {
        assert!(matches!(
            apply_entry(
                &mut store,
                index as u64 + 1,
                command(
                    &format!("insert-{index}"),
                    Action::InsertUnique { model: "user".into(), record: value }
                ),
            ),
            Outcome::Record { .. }
        ));
    }
    let increment = |key: &str, id: &str, amount: i64| {
        command(
            key,
            Action::IncrementOne {
                model: "user".into(),
                predicates: vec![predicate("id", json!(id))],
                increment: BTreeMap::from([("value".into(), serde_json::Number::from(amount))]),
                set: Record::new(),
            },
        )
    };
    assert!(
        matches!(apply_entry(&mut store, 5, increment("negative-inc", "negative", -3)), Outcome::Record { record } if record["value"] == -8)
    );
    assert!(matches!(
        apply_entry(&mut store, 6, increment("minimum-inc", "minimum", -1)),
        Outcome::Invalid { .. }
    ));
    assert_eq!(store.read_record("user", "minimum").unwrap().unwrap()["value"], i64::MIN);
    assert!(matches!(
        apply_entry(&mut store, 7, increment("maximum-inc", "maximum", 1)),
        Outcome::Invalid { .. }
    ));
    assert_eq!(store.read_record("user", "maximum").unwrap().unwrap()["value"], u64::MAX);
    assert!(
        matches!(apply_entry(&mut store, 8, increment("exact-inc", "exact", 1)), Outcome::Record { record } if record["value"] == 9_007_199_254_740_994u64)
    );
}

#[test]
fn increment_unique_violation_and_changed_idempotency_key_do_not_mutate_records() {
    let dir = tempfile::tempdir().unwrap();
    let mut store = open_store(&dir);
    assert_eq!(
        apply_entry(
            &mut store,
            1,
            command(
                "index",
                Action::CreateUniqueIndex { model: "user".into(), fields: vec!["email".into()] }
            ),
        ),
        Outcome::Applied
    );
    for (index, id, email) in [(2, "a", "a@test"), (3, "b", "b@test")] {
        let mut value = record(id, 0);
        value.insert("email".into(), json!(email));
        assert!(matches!(
            apply_entry(
                &mut store,
                index,
                command(
                    &format!("insert-{id}"),
                    Action::InsertUnique { model: "user".into(), record: value }
                )
            ),
            Outcome::Record { .. }
        ));
    }
    let mut update = Record::new();
    update.insert("email".into(), json!("b@test"));
    assert_eq!(
        apply_entry(
            &mut store,
            4,
            command(
                "unique-increment",
                Action::IncrementOne {
                    model: "user".into(),
                    predicates: vec![predicate("id", json!("a"))],
                    increment: BTreeMap::new(),
                    set: update,
                },
            ),
        ),
        Outcome::UniqueConflict
    );
    assert_eq!(store.read_record("user", "a").unwrap().unwrap()["email"], "a@test");

    let mut first = record("first", 1);
    first.insert("email".into(), json!("first@test"));
    assert!(matches!(
        apply_entry(
            &mut store,
            5,
            command("changed-key", Action::InsertUnique { model: "user".into(), record: first })
        ),
        Outcome::Record { .. }
    ));
    let mut second = record("second", 1);
    second.insert("email".into(), json!("second@test"));
    assert_eq!(
        apply_entry(
            &mut store,
            6,
            command("changed-key", Action::InsertUnique { model: "user".into(), record: second })
        ),
        Outcome::IdempotencyConflict
    );
    assert!(store.read_record("user", "second").unwrap().is_none());
}
