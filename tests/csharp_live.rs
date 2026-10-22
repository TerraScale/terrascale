//! Obsolete external-producer tests pending removal; SDK contracts use HTTP fakes.
//! Ignored by ordinary cargo test; explicitly invoking either test without
//! the runner's required configuration fails instead of silently skipping.

use std::time::Duration;

use serde_json::{json, value::RawValue, Value};
use terrascale::v1::*;
use uuid::Uuid;

struct Live {
    client: Client,
}

impl Live {
    fn required(name: &str) -> String {
        std::env::var(name)
            .ok()
            .filter(|v| !v.is_empty())
            .unwrap_or_else(|| panic!("{name} is required by the obsolete external-producer test"))
    }

    fn new() -> Self {
        let url = Self::required("TB_CSHARP_URL");
        let key = Self::required("TB_CSHARP_KEY");
        let project = Self::required("TB_CSHARP_PROJECT");
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
        Self { client }
    }
}

fn id(name: &str) -> Uuid {
    uuidv5_for("csharp-live", name)
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
