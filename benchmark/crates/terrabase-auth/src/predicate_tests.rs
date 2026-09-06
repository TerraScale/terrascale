use serde_json::json;

use crate::*;
use openraft::storage::RaftStateMachine;
use openraft::{CommittedLeaderId, Entry, EntryPayload, LogId};

fn command(key: &str, action: Action) -> Command {
    Command { idempotency_key: key.into(), observed_at_ms: 1, action }
}

fn predicate(field: &str, value: serde_json::Value, connector: Connector) -> Predicate {
    Predicate {
        field: field.into(),
        value,
        operator: Operator::Eq,
        connector,
        mode: Mode::Sensitive,
    }
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

fn guarded_email_predicates() -> Vec<Predicate> {
    vec![
        predicate("tenant", json!("a"), Connector::AND),
        predicate("email", json!("x@example"), Connector::OR),
        predicate("email", json!("y@example"), Connector::OR),
    ]
}

#[tokio::test]
async fn predicate_or_group_cannot_bypass_tenant_guard_across_operations() {
    let dir = tempfile::tempdir().unwrap();
    let mut store = SqliteStore::open(&dir.path().join("predicates.sqlite"), "predicates").unwrap();
    for (index, (id, tenant, email)) in
        [("a-x", "a", "x@example"), ("a-y", "a", "y@example"), ("b-y", "b", "y@example")]
            .into_iter()
            .enumerate()
    {
        assert!(matches!(
            apply(
                &mut store,
                index as u64 + 1,
                id,
                Action::InsertUnique {
                    model: "user".into(),
                    record: serde_json::from_value(json!({"id":id,"tenant":tenant,"email":email}))
                        .unwrap(),
                }
            )
            .await,
            Outcome::Record { .. }
        ));
    }
    let find = apply(
        &mut store,
        4,
        "find",
        Action::Find {
            model: "user".into(),
            predicates: guarded_email_predicates(),
            offset: 0,
            limit: 10,
            select: None,
            sort: None,
            joins: vec![],
        },
    )
    .await;
    let Outcome::Records { records } = find else { panic!("expected records") };
    assert_eq!(records.len(), 2);
    assert!(records.iter().all(|record| record["tenant"] == "a"));

    assert_eq!(
        apply(
            &mut store,
            5,
            "delete",
            Action::DeleteMany { model: "user".into(), predicates: guarded_email_predicates() }
        )
        .await,
        Outcome::Count { count: 2 }
    );
    let remaining = apply(
        &mut store,
        6,
        "remaining",
        Action::Count { model: "user".into(), predicates: vec![] },
    )
    .await;
    assert_eq!(remaining, Outcome::Count { count: 1 });
}

#[test]
fn sql_null_membership_semantics_are_explicit() {
    let missing = Record::new();
    assert!(crate::query::matches(&missing, &[predicate("value", json!(null), Connector::AND)]));
    assert!(!crate::query::matches(&missing, &[predicate("value", json!(1), Connector::AND)]));
    assert!(!crate::query::matches(
        &missing,
        &[Predicate {
            field: "value".into(),
            value: json!(1),
            operator: Operator::Ne,
            connector: Connector::AND,
            mode: Mode::Sensitive,
        }]
    ));
    let present = serde_json::from_value(json!({"value": 1})).unwrap();
    assert!(crate::query::matches(
        &present,
        &[Predicate {
            field: "value".into(),
            value: json!(null),
            operator: Operator::Ne,
            connector: Connector::AND,
            mode: Mode::Sensitive,
        }]
    ));
    assert!(!crate::query::matches(
        &missing,
        &[Predicate {
            field: "value".into(),
            value: json!([1, 2]),
            operator: Operator::NotIn,
            connector: Connector::AND,
            mode: Mode::Sensitive,
        }]
    ));
    assert!(crate::query::matches(
        &missing,
        &[Predicate {
            field: "value".into(),
            value: json!([]),
            operator: Operator::NotIn,
            connector: Connector::AND,
            mode: Mode::Sensitive,
        }]
    ));
}
