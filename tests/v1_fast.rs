//! Fast v1 suite: the `terrascale::v1` client against the in-process HTTPS
//! contract fixture (tests/support/v1_fixture.rs).

mod support;

use std::time::Duration;

use serde::{Deserialize, Serialize};
use serde_json::json;
use serde_json::value::RawValue;
use support::v1_fixture::{Fault, Fixture};
use terrascale::v1::*;

fn data(f: &Fixture) -> Client {
    data_with(f, &f.data_token)
}

fn data_with(f: &Fixture, token: &str) -> Client {
    Client::builder(&f.url, token)
        .add_root_ca_pem(&f.ca_pem)
        .retry_policy(RetryPolicy {
            base_delay: Duration::from_millis(1),
            max_delay: Duration::from_millis(5),
            ..RetryPolicy::default()
        })
        .build()
        .unwrap()
}

fn control(f: &Fixture) -> ControlClient {
    Client::builder(&f.url, &f.control_token)
        .add_root_ca_pem(&f.ca_pem)
        .build_control()
        .unwrap()
}

fn key(s: &str) -> IdempotencyKey {
    IdempotencyKey::new(s).unwrap()
}

#[derive(Debug, Clone, PartialEq, Serialize, Deserialize)]
struct User {
    email: String,
    name: String,
    #[serde(default)]
    logins: i64,
    #[serde(default)]
    tags: Vec<String>,
}

fn ana() -> User {
    User {
        email: "ana@example.com".into(),
        name: "Ana".into(),
        logins: 0,
        tags: vec![],
    }
}

#[test]
fn ids_names_and_builder_validation() {
    assert_eq!(
        uuidv5_for("users", "ana@example.com").to_string(),
        "b85e5c40-9a0a-517f-ba69-779a0cd2b159"
    );
    assert!(is_valid_name("users") && is_valid_name("a-b_9"));
    assert!(!is_valid_name("Users") && !is_valid_name("9a") && !is_valid_name(""));
    assert!(Version::new("tbv1-0123456789abcdef0123456789abcdef").is_ok());
    assert!(Version::new("tbv1-0123456789ABCDEF0123456789abcdef").is_err());
    assert!(IdempotencyKey::new("a.b-c_D9").is_ok());
    assert!(IdempotencyKey::new("").is_err() && IdempotencyKey::new("a b").is_err());
    assert!(IdempotencyKey::new("x".repeat(65)).is_err());
    let derived = IdempotencyKey::derive(&["refresh", "abc"]);
    assert_eq!(derived, IdempotencyKey::derive(&["refresh", "abc"]));
    assert_ne!(derived, IdempotencyKey::derive(&["refres", "habc"]));
    assert_eq!(derived.as_str().len(), 64);
    assert_ne!(IdempotencyKey::random(), IdempotencyKey::random());

    let token = format!("tbk1.{}.{}", "a".repeat(22), "b".repeat(43));
    let bad = |url: &str, tok: &str| {
        matches!(
            Client::builder(url, tok).build(),
            Err(Error::InvalidInput(_))
        )
    };
    assert!(bad("https://example.com", "tbk1.short.key"));
    assert!(bad("http://example.com", &token));
    assert!(bad("https://example.com?x=1", &token));
    assert!(bad("ftp://example.com", &token));
    assert!(Client::builder("http://127.0.0.1:1", &token)
        .build()
        .is_ok());
    assert!(Client::builder("https://example.com/prefix/", &token)
        .build()
        .is_ok());
    assert!(matches!(
        Client::builder("https://example.com", &token)
            .add_root_ca_pem("not a pem")
            .build(),
        Err(Error::InvalidInput(_))
    ));
}

#[test]
fn numbers_and_filters_encode_exactly() {
    let n: Number = "123456789012345678901234567890.000000000000000000001"
        .parse()
        .unwrap();
    assert_eq!(
        serde_json::to_string(&n).unwrap(),
        "123456789012345678901234567890.000000000000000000001"
    );
    let back: Number = serde_json::from_str("1e400").unwrap();
    assert_eq!(back.as_str(), "1e400");
    assert!("01".parse::<Number>().is_err() && "1.".parse::<Number>().is_err());
    assert!(serde_json::from_str::<Number>("\"1\"").is_err());

    let f = Filter::and([
        Filter::eq(Path::lower("/email"), "Ana@Example.com"),
        Filter::gte("/n", n.clone()),
        Filter::in_("$id", [uuidv5_for("users", "a")]),
        Filter::not(Filter::exists("/deleted_at")),
    ]);
    assert_eq!(
        serde_json::to_string(&f).unwrap(),
        format!(
            r#"{{"and":[{{"eq":[{{"lower":"/email"}},"Ana@Example.com"]}},{{"gte":["/n",{n}]}},{{"in":["$id",["{}"]]}},{{"not":{{"exists":"/deleted_at"}}}}]}}"#,
            uuidv5_for("users", "a")
        )
    );
    assert!(Filter::and([]).validate().is_err());
    assert!(Filter::lt("/n", Scalar::Null).validate().is_err());
    assert!(Filter::from_json("[1]").is_err());
    let raw = Filter::from_json(r#"{"eq":["/a",1]}"#).unwrap();
    assert_eq!(serde_json::to_string(&raw).unwrap(), r#"{"eq":["/a",1]}"#);
    assert_eq!(raw, Filter::eq("/a", 1));
    let ops = Patch::ops(vec![
        PatchOp::set("/profile/name", "Ana").unwrap(),
        PatchOp::unset("/legacy"),
        PatchOp::inc("/db_count", 1),
        PatchOp::append("/tags", "beta").unwrap(),
    ]);
    let Patch::Ops(list) = ops else {
        unreachable!()
    };
    assert_eq!(
        serde_json::to_string(&list).unwrap(),
        r#"[{"op":"set","path":"/profile/name","value":"Ana"},{"op":"unset","path":"/legacy"},{"op":"inc","path":"/db_count","by":1},{"op":"append","path":"/tags","value":"beta"}]"#
    );
}

#[tokio::test]
async fn tls_requires_the_configured_root() {
    let f = Fixture::start().await;
    let untrusted = Client::builder(&f.url, &f.data_token)
        .retry_policy(RetryPolicy::none())
        .build()
        .unwrap();
    let err = untrusted
        .database("main")
        .collection("users")
        .get::<User>(uuidv5_for("users", "x"))
        .await
        .unwrap_err();
    assert!(matches!(err, Error::Transport(_)), "{err:?}");
}

#[tokio::test]
async fn document_crud_preconditions_and_tombstones() {
    let f = Fixture::start().await;
    let users = data(&f).database("main").collection("users");
    let id = uuidv5_for("users", "ana@example.com");

    let w1 = users
        .put(id, &ana(), WriteOptions::new().if_none_match_any())
        .await
        .unwrap();
    assert!(
        w1.created && w1.id == id && w1.durability == Durability::LocalDurable && w1.offset >= 1
    );
    let again = users
        .put(id, &ana(), WriteOptions::new().if_none_match_any())
        .await
        .unwrap_err();
    assert!(
        matches!(again, Error::PreconditionFailed { op: None }),
        "{again:?}"
    );

    let doc = users.get::<User>(id).await.unwrap();
    assert_eq!(doc.version, w1.version);
    assert_eq!(doc.value(), Some(&ana()));

    let mut renamed = ana();
    renamed.name = "Ana Maria".into();
    let w2 = users
        .put(
            id,
            &renamed,
            WriteOptions::new().if_match(w1.version.clone()),
        )
        .await
        .unwrap();
    assert!(!w2.created && w2.offset > w1.offset && w2.version != w1.version);
    let stale = users
        .put(id, &renamed, w1.version.clone().into_if_match())
        .await
        .unwrap_err();
    assert!(matches!(stale, Error::PreconditionFailed { .. }));
    assert_eq!(stale.status(), Some(412));
    assert_eq!(stale.code(), Some("precondition_failed"));

    // 304 when unchanged, a document when changed.
    assert!(users
        .get_if_changed::<User>(id, &w2.version)
        .await
        .unwrap()
        .is_none());
    assert!(users
        .get_if_changed::<User>(id, &w1.version)
        .await
        .unwrap()
        .is_some());

    let w3 = users
        .patch(
            id,
            &Patch::merge(&json!({"name": "Ana M", "tags": ["a"]})).unwrap(),
            WriteOptions::new().if_match(w2.version.clone()),
        )
        .await
        .unwrap();
    let w4 = users
        .patch(
            id,
            &Patch::ops(vec![
                PatchOp::inc("/logins", 41),
                PatchOp::inc("/logins", 1),
                PatchOp::append("/tags", "beta").unwrap(),
                PatchOp::set("/name", "Ana").unwrap(),
            ]),
            WriteOptions::new(),
        )
        .await
        .unwrap();
    assert!(w4.offset > w3.offset);
    let user = users.get::<User>(id).await.unwrap().into_value().unwrap();
    assert_eq!(
        (user.logins, user.tags.clone(), user.name.as_str()),
        (42, vec!["a".to_string(), "beta".to_string()], "Ana")
    );
    let conflict = users
        .patch(
            id,
            &Patch::ops(vec![PatchOp::inc("/name", 1)]),
            WriteOptions::new(),
        )
        .await
        .unwrap_err();
    assert!(
        matches!(&conflict, Error::Api { status: 409, code, .. } if code == "patch_conflict"),
        "{conflict:?}"
    );
    assert!(matches!(
        users
            .patch(id, &Patch::ops(vec![]), WriteOptions::new())
            .await,
        Err(Error::InvalidInput(_))
    ));

    let del = users
        .delete(id, WriteOptions::new().if_match(w4.version.clone()))
        .await
        .unwrap();
    let missing = users.get::<User>(id).await.unwrap_err();
    match &missing {
        Error::NotFound {
            etag: Some(v),
            op: None,
        } => assert_eq!(v, &del.version),
        other => panic!("{other:?}"),
    }
    assert!(missing.is_not_found());
    assert!(users
        .delete(id, WriteOptions::new())
        .await
        .unwrap_err()
        .is_not_found());
    assert!(users
        .patch(
            id,
            &Patch::merge(&json!({"a": 1})).unwrap(),
            WriteOptions::new()
        )
        .await
        .unwrap_err()
        .is_not_found());
    // Recreate over the tombstone with If-Match.
    let re = users
        .put(id, &ana(), WriteOptions::new().if_match(del.version))
        .await
        .unwrap();
    assert!(re.created);
}

trait IntoIfMatch {
    fn into_if_match(self) -> WriteOptions;
}
impl IntoIfMatch for Version {
    fn into_if_match(self) -> WriteOptions {
        Precondition::IfMatch(self).into()
    }
}

#[tokio::test]
async fn create_raw_bodies_and_exact_numbers() {
    let f = Fixture::start().await;
    let items = data(&f).database("main").collection("items");
    let body =
        r#"{"big":123456789012345678901234567890.000000000000000000001,"n":-0.5e-7,"s":"x"}"#;
    let raw = RawValue::from_string(body.into()).unwrap();
    let w = items.create(&raw, WriteOptions::new()).await.unwrap();
    assert!(w.created);
    assert_eq!(
        w.location.as_deref(),
        Some(format!("/v1/databases/main/collections/items/documents/{}", w.id).as_str())
    );
    assert_eq!(w.id.get_version_num(), 7);
    let doc = items.get::<Box<RawValue>>(w.id).await.unwrap();
    assert_eq!(doc.value().unwrap().get(), body);

    #[derive(Deserialize)]
    struct Exact {
        big: Number,
        n: Number,
    }
    let exact = items
        .get::<Exact>(w.id)
        .await
        .unwrap()
        .into_value()
        .unwrap();
    assert_eq!(
        exact.big.as_str(),
        "123456789012345678901234567890.000000000000000000001"
    );
    assert_eq!(exact.n.as_str(), "-0.5e-7");

    assert!(matches!(
        items.create(&json!([1]), WriteOptions::new()).await,
        Err(Error::InvalidInput(_))
    ));
    assert!(matches!(
        items
            .create(&json!({}), WriteOptions::new().if_none_match_any())
            .await,
        Err(Error::InvalidInput(_))
    ));
    assert!(matches!(
        data(&f)
            .database("Main")
            .collection("items")
            .get::<Box<RawValue>>(w.id)
            .await,
        Err(Error::InvalidInput(_))
    ));
}

#[tokio::test]
async fn if_none_match_create_race_has_one_winner() {
    let f = Fixture::start().await;
    let orgs = data(&f).database("main").collection("orgs");
    let id = uuidv5_for("orgs", "acme");
    let tasks: Vec<_> = (0..24)
        .map(|i| {
            let orgs = orgs.clone();
            tokio::spawn(async move {
                orgs.put(
                    id,
                    &json!({"writer": i}),
                    WriteOptions::new().if_none_match_any(),
                )
                .await
            })
        })
        .collect();
    let mut wins = 0;
    for t in tasks {
        match t.await.unwrap() {
            Ok(w) => {
                assert!(w.created);
                wins += 1;
            }
            Err(Error::PreconditionFailed { .. }) => {}
            Err(e) => panic!("{e:?}"),
        }
    }
    assert_eq!(wins, 1);
}

#[tokio::test]
async fn unique_constraint_violations_name_the_constraint() {
    let f = Fixture::start().await;
    let users = data(&f).database("main").collection("users");
    users.create(&ana(), WriteOptions::new()).await.unwrap();
    let mut dup = ana();
    dup.email = "ANA@example.COM".into();
    let err = users.create(&dup, WriteOptions::new()).await.unwrap_err();
    match err {
        Error::UniqueViolation {
            constraint,
            op: None,
        } => assert_eq!(constraint, "email"),
        other => panic!("{other:?}"),
    }
}

#[tokio::test]
async fn conflicted_documents_expose_siblings_and_resolve_with_if_match() {
    let f = Fixture::start().await;
    let items = data(&f).database("main").collection("items");
    let id = uuidv5_for("items", "c");
    let version = f.seed_conflict("main", "items", &id.to_string(), r#"{"v":1}"#, r#"{"v":2}"#);
    let doc = items.get::<serde_json::Value>(id).await.unwrap();
    assert!(doc.is_conflict() && doc.value().is_none());
    let DocumentState::Conflict(sibs) = &doc.state else {
        unreachable!()
    };
    assert_eq!(sibs.len(), 2);
    assert_eq!(
        (sibs[0].event.sequence, sibs[1].document.clone()),
        (12, Some(json!({"v": 2})))
    );
    assert!(matches!(
        doc.clone().into_value(),
        Err(Error::ConflictUnresolved { .. })
    ));
    let err = items
        .patch(
            id,
            &Patch::merge(&json!({"v": 3})).unwrap(),
            WriteOptions::new(),
        )
        .await
        .unwrap_err();
    assert!(
        matches!(err, Error::ConflictUnresolved { op: None }),
        "{err:?}"
    );
    items
        .put(
            id,
            &json!({"v": 3}),
            WriteOptions::new().if_match(version.parse().unwrap()),
        )
        .await
        .unwrap();
    assert_eq!(
        items
            .get::<serde_json::Value>(id)
            .await
            .unwrap()
            .into_value()
            .unwrap(),
        json!({"v": 3})
    );
}

#[tokio::test]
async fn transactions_commit_atomically_with_op_indexed_errors() {
    let f = Fixture::start().await;
    let db = data(&f).database("main");
    let users = db.collection("users");
    let ana_id = uuidv5_for("users", "ana@example.com");
    let w = users
        .put(ana_id, &ana(), WriteOptions::new())
        .await
        .unwrap();
    let org = uuidv5_for("orgs", "acme");
    let bob = uuidv5_for("users", "bob@example.com");
    users
        .put(
            bob,
            &json!({"email": "bob@example.com", "name": "Bob"}),
            WriteOptions::new(),
        )
        .await
        .unwrap();

    let mut tx = Tx::new();
    tx.create("orgs", Some(org), &json!({"name": "Acme", "owner": ana_id}))
        .create("items", None, &json!({"org": org}))
        .patch(
            "users",
            ana_id,
            Patch::ops(vec![PatchOp::inc("/logins", 1)]),
            Some(w.version.clone()),
        )
        .assert(
            "users",
            bob,
            Filter::eq(Path::lower("/email"), "BOB@example.com"),
        )
        .assert_absent("orgs", uuidv5_for("orgs", "other"))
        .assert_count(
            "tokens",
            Filter::eq("/user", ana_id),
            CountBounds::at_most(24),
        );
    let res = db.commit(&tx).await.unwrap();
    assert_eq!(res.results.len(), 6);
    assert_eq!(res.results[0].id(), Some(org));
    assert!(res.results[1]
        .id()
        .is_some_and(|id| id.get_version_num() == 7));
    let patched = res.results[2].version().unwrap().clone();
    assert_eq!(res.results[3], OpResult::Asserted { count: None });
    assert_eq!(res.results[5], OpResult::Asserted { count: Some(0) });
    assert_eq!(users.get::<User>(ana_id).await.unwrap().version, patched);

    // A failing op names its index, and nothing is written.
    let sess = uuidv5_for("sessions", "s1");
    let mut tx = Tx::new();
    tx.create("sessions", Some(sess), &json!({"user": ana_id}))
        .put(
            "users",
            ana_id,
            &ana(),
            Some(Precondition::IfMatch(w.version.clone())),
        );
    let err = db.commit(&tx).await.unwrap_err();
    assert!(
        matches!(err, Error::PreconditionFailed { op: Some(1) }),
        "{err:?}"
    );
    assert!(db
        .collection("sessions")
        .get::<Box<RawValue>>(sess)
        .await
        .unwrap_err()
        .is_not_found());

    let mut tx = Tx::new();
    tx.create("users", None, &json!({"email": "x@y"})).create(
        "users",
        None,
        &json!({"email": "X@Y"}),
    );
    match db.commit(&tx).await.unwrap_err() {
        Error::UniqueViolation { constraint, op } => {
            assert_eq!((constraint.as_str(), op), ("email", Some(1)))
        }
        other => panic!("{other:?}"),
    }

    let mut tx = Tx::new();
    tx.assert_count("orgs", Filter::exists("/name"), CountBounds::at_least(2));
    assert!(matches!(
        db.commit(&tx).await,
        Err(Error::PreconditionFailed { op: Some(0) })
    ));
    let mut tx = Tx::new();
    tx.assert("orgs", uuidv5_for("orgs", "nope"), Filter::exists("/name"))
        .delete("orgs", org, None);
    assert!(matches!(
        db.commit(&tx).await,
        Err(Error::PreconditionFailed { op: Some(0) })
    ));
    let mut tx = Tx::new();
    tx.delete("orgs", org, None)
        .patch("orgs", org, Patch::merge(&json!({"a": 1})).unwrap(), None);
    let dup = db.commit(&tx).await.unwrap_err();
    assert!(
        matches!(&dup, Error::Api { status: 400, code, op: Some(1), .. } if code == "invalid_request"),
        "{dup:?}"
    );
    let mut tx = Tx::new();
    tx.patch(
        "orgs",
        uuidv5_for("orgs", "missing"),
        Patch::merge(&json!({"a": 1})).unwrap(),
        None,
    );
    assert!(matches!(
        db.commit(&tx).await,
        Err(Error::NotFound { op: Some(0), .. })
    ));

    // Local validation reports the op index without sending.
    let mut tx = Tx::new();
    tx.delete("orgs", org, None)
        .create("Bad Name", None, &json!({}));
    let tx_path = "/v1/databases/main/transactions";
    let sent = f.count("POST", tx_path);
    let local = db.commit(&tx).await.unwrap_err();
    assert!(
        matches!(&local, Error::InvalidTxOp { op: 1, message } if message.contains("collection")),
        "{local:?}"
    );
    assert_eq!(local.op(), Some(1));
    assert_eq!((local.status(), local.code()), (None, None));
    assert!(matches!(
        db.commit(&Tx::new()).await,
        Err(Error::InvalidInput(_))
    ));
    let mut tx = Tx::new();
    tx.delete("orgs", org, None)
        .patch("orgs", uuidv5_for("orgs", "p"), Patch::ops(vec![]), None)
        .assert_count("orgs", Filter::exists("/a"), CountBounds::default());
    let first_bad = db.commit(&tx).await.unwrap_err();
    assert_eq!(first_bad.op(), Some(1), "{first_bad:?}");
    let mut tx = Tx::new();
    tx.assert_count("orgs", Filter::exists("/a"), CountBounds::default());
    assert_eq!(db.commit(&tx).await.unwrap_err().op(), Some(0));
    assert_eq!(
        f.count("POST", tx_path),
        sent,
        "invalid transactions are not sent"
    );
}

#[tokio::test]
async fn idempotent_replay_and_uncertain_commits() {
    let f = Fixture::start().await;
    let db = data(&f).database("main");
    let items = db.collection("items");
    let docs = "/v1/databases/main/collections/items/documents";

    let k = key("create-1");
    let a = items
        .create(
            &json!({"n": 1}),
            WriteOptions::new().idempotency_key(k.clone()),
        )
        .await
        .unwrap();
    let b = items
        .create(
            &json!({"n": 1}),
            WriteOptions::new().idempotency_key(k.clone()),
        )
        .await
        .unwrap();
    assert_eq!((a.id, &a.version, a.offset), (b.id, &b.version, b.offset));
    let page = items
        .query::<Box<RawValue>>(&Query::new().count(true))
        .await
        .unwrap();
    assert_eq!(page.count.unwrap().value, 1);
    let err = items
        .create(&json!({"n": 2}), WriteOptions::new().idempotency_key(k))
        .await
        .unwrap_err();
    assert!(matches!(err, Error::IdempotencyConflict), "{err:?}");

    // commit_uncertain after the commit: a keyed write is retried and the
    // replay returns the original result.
    let mut fault = Fault::new("POST", docs, 503, "commit_uncertain");
    fault.after_commit = true;
    f.inject(fault.clone());
    let before = f.count("POST", docs);
    let c = items
        .create(
            &json!({"n": 3}),
            WriteOptions::new().idempotency_key(key("create-3")),
        )
        .await
        .unwrap();
    assert_eq!(f.count("POST", docs) - before, 2);
    assert_eq!(
        items
            .get::<serde_json::Value>(c.id)
            .await
            .unwrap()
            .into_value()
            .unwrap(),
        json!({"n": 3})
    );

    // Without a key it is surfaced, not retried.
    f.inject(fault);
    let before = f.count("POST", docs);
    let err = items
        .create(&json!({"n": 4}), WriteOptions::new())
        .await
        .unwrap_err();
    assert!(matches!(err, Error::CommitUncertain) && err.is_uncertain());
    assert_eq!(f.count("POST", docs) - before, 1);

    // Transactions replay the same way.
    let tx_path = "/v1/databases/main/transactions";
    let mut tx = Tx::new();
    tx.create("items", None, &json!({"tx": 1}))
        .idempotency_key(key("tx-1"));
    let mut fault = Fault::new("POST", tx_path, 503, "commit_uncertain");
    fault.after_commit = true;
    f.inject(fault);
    let first = db.commit(&tx).await.unwrap();
    let replay = db.commit(&tx).await.unwrap();
    assert_eq!(first, replay);
    assert_eq!(f.count("POST", tx_path), 3);
}

#[tokio::test]
async fn retry_policy_follows_the_contract() {
    let f = Fixture::start().await;
    let items = data(&f).database("main").collection("items");
    let docs = "/v1/databases/main/collections/items/documents";
    let id = uuidv5_for("items", "r");

    // 429 is always retried, even for an unkeyed mutation.
    let mut limited = Fault::new("PUT", docs, 429, "rate_limited");
    limited.retry_after = Some("0");
    f.inject(limited.clone());
    items
        .put(id, &json!({}), WriteOptions::new())
        .await
        .unwrap();
    assert_eq!(f.faults_left(), 0);

    // Transient 503s are retried for reads and keyed writes only.
    f.inject(Fault::new("GET", docs, 503, "capacity"));
    items.get::<Box<RawValue>>(id).await.unwrap();
    f.inject(Fault::new("PUT", docs, 503, "storage_unavailable"));
    let err = items
        .put(id, &json!({}), WriteOptions::new())
        .await
        .unwrap_err();
    assert!(
        matches!(&err, Error::Api { status: 503, code, .. } if code == "storage_unavailable"),
        "{err:?}"
    );
    f.inject(Fault::new("PUT", docs, 503, "dependencies_unavailable"));
    items
        .put(
            id,
            &json!({}),
            WriteOptions::new().idempotency_key(key("p1")),
        )
        .await
        .unwrap();
    f.inject(Fault::new("GET", docs, 503, "stored_corruption"));
    assert!(matches!(
        items.get::<Box<RawValue>>(id).await,
        Err(Error::Api { status: 503, .. })
    ));

    // internal_error is retried only with a key.
    f.inject(Fault::new("GET", docs, 500, "internal_error"));
    assert!(matches!(
        items.get::<Box<RawValue>>(id).await,
        Err(Error::InternalError)
    ));
    f.inject(Fault::new("PUT", docs, 500, "internal_error"));
    items
        .put(
            id,
            &json!({"k": 2}),
            WriteOptions::new().idempotency_key(key("p2")),
        )
        .await
        .unwrap();

    // The budget is bounded and RetryPolicy::none disables retries.
    let mut many = limited.clone();
    many.remaining = 10;
    f.inject(many);
    let err = items
        .put(id, &json!({}), WriteOptions::new())
        .await
        .unwrap_err();
    assert!(
        matches!(err, Error::RateLimited { retry_after: Some(d) } if d == Duration::ZERO),
        "{err:?}"
    );
    assert_eq!(f.faults_left(), 6);
    let strict = Client::builder(&f.url, &f.data_token)
        .add_root_ca_pem(&f.ca_pem)
        .retry_policy(RetryPolicy::none())
        .build()
        .unwrap();
    let err = strict
        .database("main")
        .collection("items")
        .put(id, &json!({}), WriteOptions::new())
        .await
        .unwrap_err();
    assert!(matches!(err, Error::RateLimited { .. }));
    assert_eq!(f.faults_left(), 5);
}

#[tokio::test]
async fn queries_filter_sort_project_count_and_page() {
    let f = Fixture::start().await;
    let db = data(&f).database("main");
    let items = db.collection("items");
    let mut tx = Tx::new();
    for i in 0..25 {
        tx.create(
            "items",
            Some(uuidv5_for("items", &i.to_string())),
            &json!({"n": i, "kind": if i % 2 == 0 { "even" } else { "odd" }, "name": format!("Item {i}"), "meta": {"x": i, "y": "drop"}}),
        );
    }
    db.commit(&tx).await.unwrap();

    let q = Query::new()
        .filter(Filter::and([
            Filter::eq("/kind", "even"),
            Filter::gte("/n", 4),
        ]))
        .sort(SortKey::desc("/n"))
        .project(["/n", "/meta/x"])
        .limit(3)
        .count(true);
    let first = items.query::<serde_json::Value>(&q).await.unwrap();
    assert_eq!(
        first.count,
        Some(Count {
            value: 11,
            exact: true
        })
    );
    let ns: Vec<_> = first
        .items
        .iter()
        .map(|d| d.value().unwrap().clone())
        .collect();
    assert_eq!(
        ns,
        vec![
            json!({"n": 24, "meta": {"x": 24}}),
            json!({"n": 22, "meta": {"x": 22}}),
            json!({"n": 20, "meta": {"x": 20}})
        ]
    );
    assert!(first.next_cursor.is_some());

    let mut pages = items.pages::<serde_json::Value>(q.clone());
    let mut seen = Vec::new();
    let mut counts = Vec::new();
    while let Some(page) = pages.next_page().await.unwrap() {
        counts.push(page.count.map(|c| c.value));
        seen.extend(
            page.items
                .into_iter()
                .map(|d| d.into_value().unwrap()["n"].as_i64().unwrap()),
        );
    }
    assert_eq!(seen, vec![24, 22, 20, 18, 16, 14, 12, 10, 8, 6, 4]);
    assert_eq!(counts, vec![Some(11), None, None, None]);
    assert_eq!(seen.len(), 11);

    let all = items
        .query_all::<serde_json::Value>(
            Query::new()
                .filter(Filter::prefix(Path::lower("/name"), "item 1"))
                .limit(2),
            100,
        )
        .await
        .unwrap();
    assert_eq!(all.len(), 11);
    let too_many = items
        .query_all::<serde_json::Value>(Query::new().limit(10), 15)
        .await
        .unwrap_err();
    assert!(matches!(too_many, Error::InvalidInput(_)));

    let bad = items
        .query::<serde_json::Value>(
            &Query::new()
                .filter(Filter::eq("/kind", "odd"))
                .cursor(first.next_cursor.unwrap()),
        )
        .await
        .unwrap_err();
    assert!(
        matches!(&bad, Error::Api { status: 400, code, .. } if code == "invalid_cursor"),
        "{bad:?}"
    );
    let raw = Filter::from_json(r#"{"in":["/n",[1,2,3]]}"#).unwrap();
    assert_eq!(
        items
            .query::<Box<RawValue>>(&Query::new().filter(raw).count(true))
            .await
            .unwrap()
            .count
            .unwrap()
            .value,
        3
    );
    assert!(matches!(
        items.query::<Box<RawValue>>(&Query::new().limit(0)).await,
        Err(Error::InvalidInput(_))
    ));
}

#[tokio::test]
async fn control_plane_provisions_keys_that_work_until_revoked() {
    let f = Fixture::start().await;
    let ctl = control(&f);

    let req = CreateTenant {
        name: "Beta".into(),
        external_ref: "proj_beta".into(),
        ..CreateTenant::default()
    };
    let t = ctl.create_tenant(&key("tenant-beta"), &req).await.unwrap();
    let replay = ctl.create_tenant(&key("tenant-beta"), &req).await.unwrap();
    assert_eq!(t, replay);
    assert_eq!(t.limits.max_storage_bytes, 10_737_418_240);
    let taken = ctl
        .create_tenant(&key("tenant-beta-2"), &req)
        .await
        .unwrap_err();
    assert!(matches!(&taken, Error::Api { status: 409, code, .. } if code == "name_taken"));
    assert_eq!(
        ctl.find_tenant("proj_beta").await.unwrap().unwrap().id,
        t.id
    );
    assert!(ctl.find_tenant("proj_none").await.unwrap().is_none());
    let first = ctl
        .list_tenants(
            None,
            &ListOptions {
                limit: Some(1),
                cursor: None,
            },
        )
        .await
        .unwrap();
    assert_eq!(first.items.len(), 1);
    let second = ctl
        .list_tenants(
            None,
            &ListOptions {
                limit: Some(1),
                cursor: first.next_cursor,
            },
        )
        .await
        .unwrap();
    assert_eq!(second.items[0].id, t.id);
    assert_eq!(
        ctl.get_tenant(&t.id).await.unwrap().external_ref,
        "proj_beta"
    );

    let db = ctl
        .create_database(
            &key("db-app"),
            &t.id,
            &CreateDatabase {
                name: "app".into(),
                ..CreateDatabase::default()
            },
        )
        .await
        .unwrap();
    assert_eq!(
        (
            db.name.as_str(),
            db.status.as_str(),
            db.retention.shape_log_bytes
        ),
        ("app", "ready", 1_073_741_824)
    );
    assert_eq!(
        ctl.list_databases(&t.id, &ListOptions::default())
            .await
            .unwrap()
            .items,
        vec![db.clone()]
    );

    let coll = ctl
        .create_collection(
            &key("coll-todos"),
            &t.id,
            "app",
            &CreateCollection {
                name: "todos".into(),
                ttl_path: Some("/expires_at".into()),
                unique: vec![UniqueConstraint {
                    name: "email".into(),
                    paths: vec!["/email".into()],
                    transform: Some("lower".into()),
                }],
                ..CreateCollection::default()
            },
        )
        .await
        .unwrap();
    assert_eq!(
        (
            coll.generation,
            coll.ttl_path.as_deref(),
            coll.schema.clone()
        ),
        (1, Some("/expires_at"), None)
    );
    let cleared = ctl
        .update_collection(
            &key("coll-ttl"),
            &t.id,
            "app",
            "todos",
            &UpdateCollection {
                ttl_path: Some(None),
                ..UpdateCollection::default()
            },
        )
        .await
        .unwrap();
    assert_eq!(cleared.ttl_path, None);
    assert_eq!(cleared.unique, coll.unique);
    let schema = Schema {
        fields: vec![Field {
            name: "email".into(),
            kind: "utf8".into(),
            required: true,
            nullable: false,
            fields: vec![],
            element: None,
        }],
    };
    let conv = ctl
        .update_collection(
            &key("coll-schema"),
            &t.id,
            "app",
            "todos",
            &UpdateCollection {
                schema: Some(schema),
                ..UpdateCollection::default()
            },
        )
        .await
        .unwrap_err();
    assert!(
        matches!(&conv, Error::Api { status: 409, code, .. } if code == "codec_conversion_unsupported")
    );

    let created = ctl
        .create_key(
            &key("key-app"),
            &t.id,
            &CreateKey {
                name: "app".into(),
                grants: vec![Grant {
                    database: "app".into(),
                    collections: vec!["*".into()],
                    permissions: vec!["read".into(), "write".into()],
                }],
                ttl_seconds: Some(3600),
                ..CreateKey::default()
            },
        )
        .await
        .unwrap();
    assert!(created.key.expires_at.is_some() && created.key.revoked_at.is_none());
    assert!(!format!("{created:?}").contains(&created.token));
    let todos = data_with(&f, &created.token)
        .database("app")
        .collection("todos");
    let w = todos
        .create(&json!({"email": "a@b.c"}), WriteOptions::new())
        .await
        .unwrap();
    assert!(matches!(
        todos
            .create(&json!({"email": "A@b.c"}), WriteOptions::new())
            .await,
        Err(Error::UniqueViolation { .. })
    ));
    let other_db = data_with(&f, &created.token)
        .database("main")
        .collection("users");
    assert!(matches!(
        other_db.get::<Box<RawValue>>(w.id).await,
        Err(Error::Forbidden { .. })
    ));

    // Rotation issues a working key; revocation takes effect immediately.
    let rotated = ctl
        .rotate_key(&key("key-rotate"), &t.id, &created.key.id, Some(0))
        .await
        .unwrap();
    let todos2 = data_with(&f, &rotated.token)
        .database("app")
        .collection("todos");
    todos2.get::<Box<RawValue>>(w.id).await.unwrap();
    tokio::time::sleep(Duration::from_millis(5)).await;
    assert!(matches!(
        todos.get::<Box<RawValue>>(w.id).await,
        Err(Error::InvalidKey)
    ));
    let revoked = ctl
        .revoke_key(&key("key-revoke"), &t.id, &rotated.key.id)
        .await
        .unwrap();
    assert!(revoked.revoked_at.is_some());
    assert!(matches!(
        todos2.get::<Box<RawValue>>(w.id).await,
        Err(Error::InvalidKey)
    ));
    assert_eq!(
        ctl.get_key(&t.id, &rotated.key.id)
            .await
            .unwrap()
            .revoked_at,
        revoked.revoked_at
    );
    assert_eq!(
        ctl.list_keys(&t.id, &ListOptions::default())
            .await
            .unwrap()
            .items
            .len(),
        2
    );

    // Roles: control keys cannot read documents; data keys cannot manage.
    let as_control = Client::builder(&f.url, &f.control_token)
        .add_root_ca_pem(&f.ca_pem)
        .build()
        .unwrap();
    assert!(matches!(
        as_control
            .database("main")
            .collection("users")
            .get::<Box<RawValue>>(w.id)
            .await,
        Err(Error::Forbidden { .. })
    ));
    assert!(matches!(
        data(&f).control().get_tenant(&t.id).await,
        Err(Error::Forbidden { .. })
    ));

    let op = ctl
        .delete_collection(&key("coll-del"), &t.id, "app", "todos")
        .await
        .unwrap();
    match ctl
        .wait_operation(&op.id, Duration::from_millis(1))
        .await
        .unwrap_err()
    {
        Error::OperationFailed { id, code, message } => assert_eq!(
            (id, code.as_str(), message.as_deref()),
            (
                op.id,
                "storage_unavailable",
                Some("object store unreachable")
            )
        ),
        other => panic!("{other:?}"),
    }
    let op = ctl
        .delete_database(&key("db-del"), &t.id, "app")
        .await
        .unwrap();
    assert_eq!(op.status, "running");
    let done = ctl
        .wait_operation(&op.id, Duration::from_millis(1))
        .await
        .unwrap();
    assert!(done.status == "succeeded" && done.finished_at.is_some());
    assert_eq!(
        ctl.list_regions(&ListOptions::default())
            .await
            .unwrap()
            .items[0]
            .code,
        "gru"
    );
    assert!(ctl
        .get_tenant("00000000-0000-0000-0000-000000000000")
        .await
        .unwrap_err()
        .is_not_found());
}

#[tokio::test]
async fn grants_scope_data_keys() {
    let f = Fixture::start().await;
    let token =
        f.add_data_key(json!([{"database":"main","collections":["items"],"permissions":["read"]}]));
    let c = data_with(&f, &token);
    let id = uuidv5_for("items", "g");
    assert!(c
        .database("main")
        .collection("items")
        .get::<Box<RawValue>>(id)
        .await
        .unwrap_err()
        .is_not_found());
    assert!(matches!(
        c.database("main")
            .collection("users")
            .get::<Box<RawValue>>(id)
            .await,
        Err(Error::Forbidden { .. })
    ));
    assert!(matches!(
        c.database("main")
            .collection("items")
            .put(id, &json!({}), WriteOptions::new())
            .await,
        Err(Error::Forbidden { .. })
    ));
    assert!(matches!(
        c.database("other")
            .collection("items")
            .get::<Box<RawValue>>(id)
            .await,
        Err(Error::Forbidden { .. })
    ));
    let wildcard = data(&f);
    let err = wildcard
        .database("other")
        .collection("items")
        .get::<Box<RawValue>>(id)
        .await
        .unwrap_err();
    assert!(
        matches!(&err, Error::Api { status: 404, code, .. } if code == "unknown_database"),
        "{err:?}"
    );
    let bogus = Client::builder(
        &f.url,
        format!("tbk1.{}.{}", "a".repeat(22), "b".repeat(43)),
    )
    .add_root_ca_pem(&f.ca_pem)
    .build()
    .unwrap();
    assert!(matches!(
        bogus
            .database("main")
            .collection("items")
            .get::<Box<RawValue>>(id)
            .await,
        Err(Error::InvalidKey)
    ));
}

#[tokio::test]
async fn oversized_responses_are_not_retried() {
    let f = Fixture::start().await;
    let items = data(&f).database("main").collection("items");
    let id = uuidv5_for("items", "big");
    items
        .put(id, &json!({"pad": "x".repeat(4096)}), WriteOptions::new())
        .await
        .unwrap();
    let small = Client::builder(&f.url, &f.data_token)
        .add_root_ca_pem(&f.ca_pem)
        .max_response_bytes(1024)
        .build()
        .unwrap();
    let path = format!("/v1/databases/main/collections/items/documents/{id}");
    let before = f.count("GET", &path);
    let err = small
        .database("main")
        .collection("items")
        .get::<Box<RawValue>>(id)
        .await
        .unwrap_err();
    assert!(
        matches!(err, Error::ResponseTooLarge { limit: 1024 }),
        "{err:?}"
    );
    assert!(!err.is_uncertain());
    assert_eq!(f.count("GET", &path) - before, 1, "sent exactly once");
}

#[tokio::test]
async fn only_rate_limited_429s_are_retried() {
    let f = Fixture::start().await;
    let items = data(&f).database("main").collection("items");
    let docs = "/v1/databases/main/collections/items/documents";
    let id = uuidv5_for("items", "429");
    let path = format!("{docs}/{id}");

    let mut unknown = Fault::new("PUT", docs, 429, "slow_down");
    unknown.retry_after = Some("0");
    f.inject(unknown);
    let before = f.count("PUT", &path);
    let err = items
        .put(id, &json!({}), WriteOptions::new())
        .await
        .unwrap_err();
    assert!(
        matches!(&err, Error::Api { status: 429, code, .. } if code == "slow_down"),
        "{err:?}"
    );
    assert_eq!(f.count("PUT", &path) - before, 1);

    let mut malformed = Fault::new("PUT", docs, 429, "rate_limited");
    malformed.body = Some("busy");
    f.inject(malformed);
    let before = f.count("PUT", &path);
    let err = items
        .put(id, &json!({}), WriteOptions::new())
        .await
        .unwrap_err();
    assert!(
        matches!(&err, Error::Api { status: 429, code, .. } if code == "http_429"),
        "{err:?}"
    );
    assert_eq!(f.count("PUT", &path) - before, 1);
    assert_eq!(f.faults_left(), 0);
}

#[tokio::test]
async fn lower_uses_unicode_default_case_folding() {
    let f = Fixture::start().await;
    let users = data(&f).database("main").collection("users");
    users
        .create(
            &json!({"email": "straße@example.com", "name": "Straße"}),
            WriteOptions::new(),
        )
        .await
        .unwrap();
    match users
        .create(
            &json!({"email": "STRASSE@example.com", "name": "x"}),
            WriteOptions::new(),
        )
        .await
        .unwrap_err()
    {
        Error::UniqueViolation { constraint, .. } => assert_eq!(constraint, "email"),
        other => panic!("{other:?}"),
    }
    let count = |filter: Filter| {
        let users = users.clone();
        async move {
            users
                .query::<Box<RawValue>>(&Query::new().filter(filter).count(true))
                .await
                .unwrap()
                .count
                .unwrap()
                .value
        }
    };
    assert_eq!(count(Filter::eq(Path::lower("/name"), "STRASSE")).await, 1);
    assert_eq!(count(Filter::eq(Path::lower("/name"), "strasse")).await, 1);
    assert_eq!(count(Filter::eq("/name", "STRASSE")).await, 0);
    assert_eq!(
        count(Filter::prefix(Path::lower("/name"), "STRAS")).await,
        1
    );
}

#[tokio::test]
async fn numbers_compare_and_round_trip_exactly() {
    let f = Fixture::start().await;
    let db = data(&f).database("main");
    let items = db.collection("items");
    let big = "123456789012345678901234567890.000000000000000000001";
    let n = |s: &str| s.parse::<Number>().unwrap();
    // Adjacent integers above 2^53 are distinct; 1 and 1.0 are equal.
    let mut tx = Tx::new();
    for (key, value) in [
        ("a", "9007199254740992"),
        ("b", "9007199254740993"),
        ("c", "9007199254740994"),
    ] {
        tx.create(
            "items",
            Some(uuidv5_for("items", key)),
            &std::collections::BTreeMap::from([("n", n(value)), ("one", n("1.0"))]),
        );
    }
    let raw = RawValue::from_string(format!(r#"{{"big":{big},"tiny":-1.5e-30}}"#)).unwrap();
    let big_id = uuidv5_for("items", "big");
    tx.create("items", Some(big_id), &raw);
    db.commit(&tx).await.unwrap();

    let count = |filter: Filter| {
        let items = items.clone();
        async move {
            items
                .query::<Box<RawValue>>(&Query::new().filter(filter).count(true))
                .await
                .unwrap()
                .count
                .unwrap()
                .value
        }
    };
    assert_eq!(count(Filter::eq("/n", n("9007199254740993"))).await, 1);
    assert_eq!(count(Filter::gt("/n", n("9007199254740992"))).await, 2);
    assert_eq!(count(Filter::lt("/n", n("9007199254740994"))).await, 2);
    assert_eq!(count(Filter::eq("/one", 1)).await, 3);
    assert_eq!(count(Filter::eq("/big", n(big))).await, 1);
    assert_eq!(
        count(Filter::lt(
            "/big",
            n("123456789012345678901234567890.000000000000000000002")
        ))
        .await,
        1
    );
    let sorted = items
        .query::<serde_json::Value>(
            &Query::new()
                .filter(Filter::exists("/n"))
                .sort(SortKey::desc("/n"))
                .project(["/n"]),
        )
        .await
        .unwrap();
    let ns: Vec<String> = sorted
        .items
        .into_iter()
        .map(|d| d.into_value().unwrap()["n"].to_string())
        .collect();
    assert_eq!(
        ns,
        ["9007199254740994", "9007199254740993", "9007199254740992"]
    );

    // Transaction, patch and projection keep the exact tokens.
    #[derive(Deserialize)]
    struct Exact {
        big: Number,
        tiny: Number,
        #[serde(default)]
        more: Option<Number>,
    }
    let got = items
        .get::<Exact>(big_id)
        .await
        .unwrap()
        .into_value()
        .unwrap();
    assert_eq!((got.big.as_str(), got.tiny.as_str()), (big, "-1.5e-30"));
    let more = "98765432109876543210987654321.123456789";
    items
        .patch(
            big_id,
            // Not json!: serde_json::Value would round a Number through f64.
            &Patch::merge(&std::collections::BTreeMap::from([("more", n(more))])).unwrap(),
            WriteOptions::new(),
        )
        .await
        .unwrap();
    let mut tx = Tx::new();
    tx.patch(
        "items",
        big_id,
        Patch::ops(vec![PatchOp::set("/nested/v", &n(big)).unwrap()]),
        None,
    );
    db.commit(&tx).await.unwrap();
    let got = items
        .get::<Exact>(big_id)
        .await
        .unwrap()
        .into_value()
        .unwrap();
    assert_eq!(
        (
            got.big.as_str(),
            got.tiny.as_str(),
            got.more.unwrap().as_str()
        ),
        (big, "-1.5e-30", more)
    );
    let projected = items
        .query::<Exact>(
            &Query::new()
                .filter(Filter::eq("$id", big_id))
                .project(["/big", "/tiny"]),
        )
        .await
        .unwrap();
    let p = projected.items[0].value().unwrap();
    assert_eq!(
        (p.big.as_str(), p.tiny.as_str(), p.more.is_none()),
        (big, "-1.5e-30", true)
    );
    let nested = items
        .query::<Box<RawValue>>(
            &Query::new()
                .filter(Filter::eq("/nested/v", n(big)))
                .project(["/nested/v"]),
        )
        .await
        .unwrap();
    assert_eq!(
        nested.items[0].value().unwrap().get(),
        format!(r#"{{"nested":{{"v":{big}}}}}"#)
    );
}

#[tokio::test]
async fn inc_is_exact_across_decimals_and_the_i64_boundary() {
    let f = Fixture::start().await;
    let items = data(&f).database("main").collection("items");
    let id = uuidv5_for("items", "inc");
    let raw = RawValue::from_string(
        r#"{"frac":0.1,"max":9223372036854775807,"min":-9223372036854775808,"big":1e20}"#.into(),
    )
    .unwrap();
    items.put(id, &raw, WriteOptions::new()).await.unwrap();
    let n = |s: &str| s.parse::<Number>().unwrap();
    items
        .patch(
            id,
            &Patch::ops(vec![
                PatchOp::inc("/frac", n("0.2")),
                PatchOp::inc("/max", 1),
                PatchOp::inc("/min", -1),
                PatchOp::inc("/big", n("0.5")),
                PatchOp::inc("/fresh", n("-2.50")),
            ]),
            WriteOptions::new(),
        )
        .await
        .unwrap();

    #[derive(Deserialize)]
    struct Counters {
        frac: Number,
        max: Number,
        min: Number,
        big: Number,
        fresh: Number,
    }
    let c = items
        .get::<Counters>(id)
        .await
        .unwrap()
        .into_value()
        .unwrap();
    assert_eq!(
        [
            c.frac.as_str(),
            c.max.as_str(),
            c.min.as_str(),
            c.big.as_str(),
            c.fresh.as_str()
        ],
        [
            "0.3",
            "9223372036854775808",
            "-9223372036854775809",
            "100000000000000000000.5",
            "-2.5"
        ]
    );
    // A result beyond 128 significant digits cannot be stored.
    let err = items
        .patch(
            id,
            &Patch::ops(vec![PatchOp::inc(
                "/big",
                n(&format!("0.{}1", "0".repeat(120))),
            )]),
            WriteOptions::new(),
        )
        .await
        .unwrap_err();
    assert!(
        matches!(&err, Error::Api { status: 409, code, .. } if code == "patch_conflict"),
        "{err:?}"
    );
}

#[tokio::test]
async fn tenant_updates_validate_before_applying() {
    let f = Fixture::start().await;
    let ctl = control(&f);
    let t = ctl.get_tenant(&f.tenant_id).await.unwrap();
    assert!(!t.advisor_external);
    let updated = ctl
        .update_tenant(
            &key("adv-on"),
            &t.id,
            &UpdateTenant {
                advisor_external: Some(true),
                ..UpdateTenant::default()
            },
        )
        .await
        .unwrap();
    assert!(updated.advisor_external);
    assert!(ctl.get_tenant(&t.id).await.unwrap().advisor_external);

    let rejected = ctl
        .update_tenant(
            &key("bad-status"),
            &t.id,
            &UpdateTenant {
                name: Some("Renamed".into()),
                status: Some("frozen".into()),
                advisor_external: Some(false),
                ..UpdateTenant::default()
            },
        )
        .await
        .unwrap_err();
    assert!(
        matches!(&rejected, Error::Api { status: 400, code, .. } if code == "invalid_request"),
        "{rejected:?}"
    );
    let after = ctl.get_tenant(&t.id).await.unwrap();
    assert_eq!(after, updated, "a rejected update changes nothing");

    let err = ctl
        .create_key(
            &key("ttl-overflow"),
            &t.id,
            &CreateKey {
                name: "x".into(),
                grants: vec![Grant {
                    database: "main".into(),
                    collections: vec!["*".into()],
                    permissions: vec!["read".into()],
                }],
                ttl_seconds: Some(u64::MAX),
                ..CreateKey::default()
            },
        )
        .await
        .unwrap_err();
    assert!(
        matches!(&err, Error::Api { status: 400, code, .. } if code == "invalid_request"),
        "{err:?}"
    );
}
