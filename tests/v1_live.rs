//! Slow v1 suite against a live TerraBase v1 server (tb-server, or ts-go's
//! `tbv1-fake` for development). Skipped unless configured:
//!
//! * `TB_V1_URL`: the origin, for example `https://localhost:9465`.
//! * `TB_V1_CONTROL_KEY`: a control key (`tbk1.…`), or a path to a file
//!   holding one.
//! * `TB_V1_CA_FILE` (optional): a PEM root CA for a development certificate.
//!
//! The suite provisions its own tenant (unique `external_ref` per run),
//! database, collection and short-lived data key, then exercises the data
//! plane and revokes the key.

use std::time::Duration;

use serde_json::json;
use serde_json::value::RawValue;
use terrascale::v1::*;

struct Live {
    url: String,
    control_key: String,
    ca: Option<String>,
}

fn live() -> Option<Live> {
    let url = std::env::var("TB_V1_URL").ok().filter(|v| !v.is_empty())?;
    let raw = std::env::var("TB_V1_CONTROL_KEY")
        .ok()
        .filter(|v| !v.is_empty())?;
    let control_key = if raw.starts_with("tbk1.") {
        raw
    } else {
        std::fs::read_to_string(&raw)
            .expect("read TB_V1_CONTROL_KEY file")
            .trim()
            .to_owned()
    };
    let ca = std::env::var("TB_V1_CA_FILE")
        .ok()
        .filter(|v| !v.is_empty())
        .map(|p| std::fs::read_to_string(p).expect("read TB_V1_CA_FILE"));
    Some(Live {
        url,
        control_key,
        ca,
    })
}

fn builder(l: &Live, token: &str) -> ClientBuilder {
    let b = Client::builder(&l.url, token);
    match &l.ca {
        Some(pem) => b.add_root_ca_pem(pem),
        None => b,
    }
}

#[tokio::test]
async fn live_provision_crud_query_and_revoke() {
    let Some(l) = live() else {
        eprintln!("skipping: set TB_V1_URL and TB_V1_CONTROL_KEY (and TB_V1_CA_FILE) to run the live v1 suite");
        return;
    };
    let run = IdempotencyKey::random();
    let k = |step: &str| IdempotencyKey::derive(&[run.as_str(), step]);
    let ctl = builder(&l, &l.control_key).build_control().unwrap();

    let tenant = ctl
        .create_tenant(
            &k("tenant"),
            &CreateTenant {
                name: "ts-rust live".into(),
                external_ref: format!("tsrust_{run}"),
                ..CreateTenant::default()
            },
        )
        .await
        .unwrap();
    assert_eq!(
        ctl.find_tenant(&tenant.external_ref)
            .await
            .unwrap()
            .map(|t| t.id),
        Some(tenant.id.clone())
    );
    let db = ctl
        .create_database(
            &k("db"),
            &tenant.id,
            &CreateDatabase {
                name: "app".into(),
                ..CreateDatabase::default()
            },
        )
        .await
        .unwrap();
    assert_eq!(db.name, "app");
    ctl.create_collection(
        &k("todos"),
        &tenant.id,
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
    let key = ctl
        .create_key(
            &k("key"),
            &tenant.id,
            &CreateKey {
                name: "ts-rust-live".into(),
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

    let data = builder(&l, &key.token).build().unwrap();
    let app = data.database("app");
    let todos = app.collection("todos");
    let id = uuidv5_for("todos", "first");
    let w1 = todos
        .put(
            id,
            &json!({"email": "a@example.com", "n": 1}),
            WriteOptions::new().if_none_match_any(),
        )
        .await
        .unwrap();
    assert!(w1.created);
    let doc = todos.get::<serde_json::Value>(id).await.unwrap();
    assert_eq!(doc.version, w1.version);
    let w2 = todos
        .put(
            id,
            &json!({"email": "a@example.com", "n": 2}),
            WriteOptions::new().if_match(w1.version.clone()),
        )
        .await
        .unwrap();
    assert!(matches!(
        todos
            .put(
                id,
                &json!({"email": "a@example.com"}),
                WriteOptions::new().if_match(w1.version.clone())
            )
            .await,
        Err(Error::PreconditionFailed { .. })
    ));
    assert!(matches!(
        todos.create(&json!({"email": "A@EXAMPLE.com"}), WriteOptions::new()).await,
        Err(Error::UniqueViolation { ref constraint, .. }) if constraint == "email"
    ));
    let w3 = todos
        .patch(
            id,
            &Patch::ops(vec![PatchOp::inc("/n", 40)]),
            WriteOptions::new().if_match(w2.version),
        )
        .await
        .unwrap();
    assert_eq!(
        todos
            .get::<serde_json::Value>(id)
            .await
            .unwrap()
            .into_value()
            .unwrap()["n"],
        json!(42)
    );

    let mut tx = Tx::new();
    tx.create("todos", None, &json!({"email": "b@example.com"}))
        .assert("todos", id, Filter::eq("/n", 42))
        .assert_count("todos", Filter::exists("/email"), CountBounds::at_most(10))
        .idempotency_key(k("tx"));
    let res = app.commit(&tx).await.unwrap();
    assert_eq!(res.results.len(), 3);
    assert_eq!(app.commit(&tx).await.unwrap(), res, "idempotent replay");

    let page = todos
        .query::<serde_json::Value>(
            &Query::new()
                .filter(Filter::exists("/email"))
                .limit(1)
                .count(true),
        )
        .await
        .unwrap();
    assert_eq!(page.count.map(|c| c.value), Some(2));
    let all = todos
        .query_all::<Box<RawValue>>(Query::new().limit(1), 10)
        .await
        .unwrap();
    assert_eq!(all.len(), 2);

    let del = todos
        .delete(id, WriteOptions::new().if_match(w3.version))
        .await
        .unwrap();
    match todos.get::<Box<RawValue>>(id).await {
        Err(Error::NotFound { etag, .. }) => assert_eq!(etag, Some(del.version)),
        other => panic!("{other:?}"),
    }

    ctl.revoke_key(&k("revoke"), &tenant.id, &key.key.id)
        .await
        .unwrap();
    let mut revoked = false;
    for _ in 0..20 {
        match todos.get::<Box<RawValue>>(id).await {
            Err(Error::InvalidKey) => {
                revoked = true;
                break;
            }
            _ => tokio::time::sleep(Duration::from_millis(100)).await,
        }
    }
    assert!(revoked, "a revoked key must fail with 401 invalid_key");
}
