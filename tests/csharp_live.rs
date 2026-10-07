//! Required real C# binary lane: `python3 scripts/terrabase/run.py`.
//! Ignored by ordinary cargo test; explicitly invoking either test without
//! the runner's required configuration fails instead of silently skipping.

use std::time::Duration;

use serde_json::{json, value::RawValue, Value};
use terrascale::v1::*;
use uuid::Uuid;

struct Live {
    client: Client,
    http: reqwest::Client,
    url: String,
    key: String,
    project: Uuid,
    region: String,
}

impl Live {
    fn required(name: &str) -> String {
        std::env::var(name)
            .ok()
            .filter(|v| !v.is_empty())
            .unwrap_or_else(|| panic!("{name} is required; run python3 scripts/terrabase/run.py"))
    }

    fn new() -> Self {
        let url = Self::required("TB_CSHARP_URL");
        let key = Self::required("TB_CSHARP_KEY");
        let project = Self::required("TB_CSHARP_PROJECT").parse().unwrap();
        let region = Self::required("TB_CSHARP_REGION");
        let ca = std::fs::read(Self::required("TB_CSHARP_CA_FILE")).unwrap();
        assert!(key.starts_with("tb_"), "expected real producer legacy key");
        let client = Client::builder(&url, &key)
            .project(project)
            .region_pin(&region)
            .add_root_ca_pem(&ca)
            .retry_policy(RetryPolicy::none())
            .timeout(Some(Duration::from_secs(30)))
            .build()
            .unwrap();
        let http = reqwest::Client::builder()
            .no_proxy()
            .redirect(reqwest::redirect::Policy::none())
            .add_root_certificate(reqwest::Certificate::from_pem(&ca).unwrap())
            .timeout(Duration::from_secs(30))
            .build()
            .unwrap();
        Self {
            client,
            http,
            url,
            key,
            project,
            region,
        }
    }

    fn point_url(&self, collection: &str, id: Uuid) -> String {
        format!(
            "{}/v1/projects/{}/databases/app/collections/{collection}/documents/{id}",
            self.url, self.project
        )
    }
}

fn id(name: &str) -> Uuid {
    uuidv5_for("csharp-live", name)
}

#[tokio::test]
#[ignore = "requires verified C# release; scripts/terrabase/run.py provisions it"]
async fn csharp_live_contract() {
    tokio::time::timeout(Duration::from_secs(180), exercise())
        .await
        .expect("C# contract checks exceeded 180 seconds");
}

async fn exercise() {
    let live = Live::new();
    let db = live.client.database("app");
    assert_eq!(db.create().await.unwrap().project_id, live.project);
    let collection = db.create_collection("items").await.unwrap();
    assert_eq!(collection.name, "items");
    let items = db.collection("items");
    assert_eq!(items.get_collection().await.unwrap(), collection);
    db.create_collection("other").await.unwrap();
    let list = db.list_collections(Some(1), None).await.unwrap();
    assert_eq!(list.items.len(), 1);
    let next = db
        .list_collections(Some(1), list.next_cursor.as_deref())
        .await
        .unwrap();
    assert_eq!(next.items.len(), 1);
    assert_ne!(next.items[0].name, list.items[0].name);
    assert!(next.next_cursor.is_none());

    // The exact decimal must survive SDK serialization and real storage.
    let raw = RawValue::from_string(
        r#"{"n":1,"decimal":123456789012345678901234567890.123456789,"nested":{"keep":1,"remove":2},"tags":[]}"#.into(),
    )
    .unwrap();
    let first = items
        .put(id("point"), &raw, WriteOptions::new().if_none_match_any())
        .await
        .unwrap();
    assert!(first.created);
    assert_eq!(first.durability, Durability::LocalDurable);
    let read = items.get::<Box<RawValue>>(id("point")).await.unwrap();
    assert_eq!(read.version, first.version);
    assert!(read
        .value()
        .unwrap()
        .get()
        .contains("123456789012345678901234567890.123456789"));
    assert!(items
        .get_if_changed::<Box<RawValue>>(id("point"), &first.version)
        .await
        .unwrap()
        .is_none());
    let merge = items
        .patch(
            id("point"),
            &Patch::merge(&json!({"nested":{"remove":null,"added":true}})).unwrap(),
            WriteOptions::new().if_match(first.version.clone()),
        )
        .await
        .unwrap();
    let conditional = items
        .put(
            id("point"),
            &json!({"wrong":true}),
            WriteOptions::new().if_match(first.version),
        )
        .await
        .unwrap_err();
    assert_eq!(conditional.status(), Some(412));
    let patched = items
        .patch(
            id("point"),
            &Patch::ops(vec![
                PatchOp::inc("/n", 41),
                PatchOp::append("/tags", &json!("sdk")).unwrap(),
                PatchOp::set("/set", &json!(true)).unwrap(),
                PatchOp::unset("/nested/keep"),
            ]),
            WriteOptions::new().if_match(merge.version),
        )
        .await
        .unwrap();
    let body = items
        .get::<Value>(id("point"))
        .await
        .unwrap()
        .into_value()
        .unwrap();
    assert_eq!(body["n"], 42);
    assert_eq!(body["nested"], json!({"added":true}));
    assert_eq!(body["tags"], json!(["sdk"]));
    assert_eq!(body["set"], true);
    assert!(items
        .get_if_changed::<Value>(id("point"), &patched.version)
        .await
        .unwrap()
        .is_none());
    let generated = items
        .create(&json!({"generated":true}), WriteOptions::new())
        .await
        .unwrap();
    assert!(generated.created);
    assert_eq!(generated.id.get_version_num(), 7);
    assert!(generated
        .location
        .unwrap()
        .contains(&generated.id.to_string()));

    // A rejected key must have no effect, even with an otherwise valid write.
    let no_dispatch = items
        .put(
            id("key-rejected"),
            &json!({}),
            WriteOptions::new().idempotency_key(IdempotencyKey::random()),
        )
        .await
        .unwrap_err();
    assert!(matches!(no_dispatch, Error::InvalidInput(_)));
    assert!(items
        .get::<Value>(id("key-rejected"))
        .await
        .unwrap_err()
        .is_not_found());
    let mut keyed_tx = Tx::new();
    keyed_tx
        .create("items", Some(id("key-rejected")), &json!({}))
        .idempotency_key(IdempotencyKey::random());
    assert!(matches!(
        db.commit(&keyed_tx).await,
        Err(Error::InvalidInput(_))
    ));

    // A failure at op 2 must roll back writes in both collections.
    let mut failed = Tx::new();
    failed
        .create("items", Some(id("rollback")), &json!({"batch":true}))
        .create("other", Some(id("rollback")), &json!({"batch":true}))
        .put(
            "items",
            id("point"),
            &json!({"wrong":true}),
            Some(Precondition::IfMatch(
                Version::new("tbv1-00000000000000000000000000000000").unwrap(),
            )),
        );
    let failure = db.commit(&failed).await.unwrap_err();
    assert_eq!(failure.status(), Some(412));
    assert_eq!(failure.op(), Some(2));
    for name in ["items", "other"] {
        assert!(db
            .collection(name)
            .get::<Value>(id("rollback"))
            .await
            .unwrap_err()
            .is_not_found());
    }
    assert_eq!(
        items.get::<Value>(id("point")).await.unwrap().version,
        patched.version
    );

    // One commit offset covers all writes; count assertions see precommit data.
    let before = items
        .put(id("before"), &json!({"anchor":true}), WriteOptions::new())
        .await
        .unwrap();
    assert_eq!(
        before.offset,
        generated.offset + 1,
        "failed transaction consumed no offset"
    );
    let mut tx = Tx::new();
    tx.create("items", Some(id("batch-a")), &json!({"batch":true,"n":1}))
        .create("items", Some(id("batch-b")), &json!({"batch":true,"n":2}))
        .create("other", Some(id("batch-c")), &json!({"batch":true}))
        .assert("items", id("point"), Filter::eq("/n", 42))
        .assert_count("items", Filter::exists("/batch"), CountBounds::at_most(0));
    let committed = db.commit(&tx).await.unwrap();
    assert_eq!(committed.durability, Durability::LocalDurable);
    assert_eq!(committed.offset, before.offset + 1);
    assert_eq!(committed.results.len(), 5);
    for (index, name, point) in [
        (0, "items", "batch-a"),
        (1, "items", "batch-b"),
        (2, "other", "batch-c"),
    ] {
        let document = db.collection(name).get::<Value>(id(point)).await.unwrap();
        assert_eq!(committed.results[index].id(), Some(document.id));
        assert_eq!(committed.results[index].version(), Some(&document.version));
    }
    assert_eq!(committed.results[4], OpResult::Asserted { count: Some(0) });
    let after = items
        .put(id("after"), &json!({"anchor":true}), WriteOptions::new())
        .await
        .unwrap();
    assert_eq!(after.offset, committed.offset + 1);

    let query = Query::new()
        .filter(Filter::eq("/batch", true))
        .sort(SortKey::asc("/n"))
        .limit(1)
        .count(true);
    let page = items.query::<Value>(&query).await.unwrap();
    assert_eq!(page.items.len(), 1);
    assert_eq!(page.items[0].id, id("batch-a"));
    assert_eq!(page.count.unwrap().value, 2);
    let second = items
        .query::<Value>(&query.clone().count(false).cursor(page.next_cursor.unwrap()))
        .await
        .unwrap();
    assert_eq!(second.items[0].id, id("batch-b"));
    assert!(second.next_cursor.is_none());
    assert_eq!(items.query_all::<Value>(query, 2).await.unwrap().len(), 2);
    let listed = items.list::<Box<RawValue>>(Some(1), None).await.unwrap();
    assert_eq!(listed.items.len(), 1);
    let listed_next = items
        .list::<Box<RawValue>>(Some(1), listed.next_cursor.as_deref())
        .await
        .unwrap();
    assert_eq!(listed_next.items.len(), 1);
    assert_ne!(listed.items[0].id, listed_next.items[0].id);

    // Region rejection is an empty 421 body in this release. Check the raw
    // contract, including that a rejected mutation never ran.
    let wrong = live
        .http
        .put(live.point_url("items", id("wrong-region")))
        .bearer_auth(&live.key)
        .header("X-TerraScale-Region-Pin", "wrong-region")
        .json(&json!({"must_not_exist":true}))
        .send()
        .await
        .unwrap();
    assert_eq!(wrong.status().as_u16(), 421);
    assert_eq!(wrong.headers()["X-TerraBase-Region"], live.region);
    assert!(items
        .get::<Value>(id("wrong-region"))
        .await
        .unwrap_err()
        .is_not_found());
    let correct = live
        .http
        .get(live.point_url("items", id("point")))
        .bearer_auth(&live.key)
        .header("X-TerraScale-Region-Pin", &live.region)
        .send()
        .await
        .unwrap();
    assert_eq!(correct.status().as_u16(), 200);
    assert_eq!(correct.headers()["X-TerraBase-Region"], live.region);

    let tombstone = items
        .delete(id("point"), WriteOptions::new().if_match(patched.version))
        .await
        .unwrap();
    match items.get::<Value>(id("point")).await.unwrap_err() {
        Error::NotFound { etag, .. } => assert_eq!(etag, Some(tombstone.version.clone())),
        other => panic!("expected 404 with tombstone ETag: {other:?}"),
    }
    db.create_collection("disposable").await.unwrap();
    assert_eq!(
        db.collection("disposable")
            .drop_collection()
            .await
            .unwrap()
            .name,
        "disposable"
    );
    let dropped = db
        .collection("disposable")
        .get_collection()
        .await
        .unwrap_err();
    assert_eq!(dropped.status(), Some(404));
    assert_eq!(dropped.code(), Some("unknown_collection"));

    // Leave an exact decimal marker and its expected version/offset in real
    // documents. The runner verifies both after a clean producer restart.
    let durable = items
        .put(id("durable"), &raw, WriteOptions::new())
        .await
        .unwrap();
    items
        .put(
            id("restart-proof"),
            &json!({"version":durable.version,"offset":durable.offset.to_string(),"tombstone":tombstone.version}),
            WriteOptions::new(),
        )
        .await
        .unwrap();
    eprintln!("C# contract passed: CRUD, exact decimals, patches, rollback, atomic offset, paging, 421, tombstones");
}

#[tokio::test]
#[ignore = "runner restarts the verified producer before this durability check"]
async fn csharp_live_restart() {
    let live = Live::new();
    let db = live.client.database("app");
    let items = db.collection("items");
    let proof = items
        .get::<Value>(id("restart-proof"))
        .await
        .unwrap()
        .into_value()
        .unwrap();
    let durable = items.get::<Box<RawValue>>(id("durable")).await.unwrap();
    assert_eq!(durable.version.as_str(), proof["version"].as_str().unwrap());
    assert!(durable
        .value()
        .unwrap()
        .get()
        .contains("123456789012345678901234567890.123456789"));
    match items.get::<Value>(id("point")).await.unwrap_err() {
        Error::NotFound {
            etag: Some(etag), ..
        } => assert_eq!(etag.as_str(), proof["tombstone"].as_str().unwrap()),
        other => panic!("tombstone did not survive restart: {other:?}"),
    }
    assert_eq!(
        items
            .query_all::<Value>(Query::new().filter(Filter::eq("/batch", true)).limit(1), 2)
            .await
            .unwrap()
            .len(),
        2
    );
    let next = items
        .put(
            id("after-restart"),
            &json!({"restarted":true}),
            WriteOptions::new(),
        )
        .await
        .unwrap();
    let offset: u64 = proof["offset"].as_str().unwrap().parse().unwrap();
    assert_eq!(
        next.offset,
        offset + 2,
        "commit offset persists across restart"
    );
    eprintln!(
        "C# restart passed: exact document, version, tombstone, query, and commit offset survived"
    );
}
