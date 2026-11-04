use super::*;
use crate::v1::{CreateDatabaseOptions, DatabasePlacement};
use serde_json::Value;

const PLACEMENT_PROJECT: &str = "01JA0000000000000000000001";
const PLACEMENT_DATABASE: &str = "01JA0000000000000000000004";

fn placement(name: &str) -> Value {
    let text = match name {
        "create-with-replicas" => {
            include_str!("../../contracts/terrabase/placement/create-with-replicas.json")
        }
        "create-all-regions" => {
            include_str!("../../contracts/terrabase/placement/create-all-regions.json")
        }
        "set-replicas" => include_str!("../../contracts/terrabase/placement/set-replicas.json"),
        "set-all-regions" => {
            include_str!("../../contracts/terrabase/placement/set-all-regions.json")
        }
        "move-home" => include_str!("../../contracts/terrabase/placement/move-home.json"),
        _ => include_str!("../../contracts/terrabase/placement/move-home-refused.json"),
    };
    serde_json::from_str(text).unwrap()
}

async fn replay(exchanges: Vec<Value>) -> Fixture {
    let next = AtomicUsize::new(0);
    Fixture::new(move |request| {
        let exchange = &exchanges[next.fetch_add(1, Ordering::SeqCst)];
        assert_eq!(request.method, exchange["request"]["method"]);
        assert_eq!(request.path, exchange["request"]["path"]);
        let body: Value = serde_json::from_slice(&request.body).unwrap();
        assert_eq!(body, exchange["request"]["body"]);
        let response = &exchange["response"];
        (
            response["status"].as_u64().unwrap() as u16,
            "application/json",
            response["body"].to_string(),
        )
    })
    .await
}

#[tokio::test]
async fn placement_matches_recorded_contract() {
    let mut unresolved = placement("move-home");
    unresolved["response"] =
        json!({"status": 503, "body": {"error": {"code": "move_unresolved", "message": "retry"}}});
    let data = replay(vec![
        placement("create-with-replicas"),
        placement("create-all-regions"),
        placement("set-replicas"),
        placement("set-all-regions"),
        unresolved,
        placement("move-home"),
    ])
    .await;
    let issuer = Fixture::new(|_| ok(grant("access", 900))).await;
    let client = data.project_client(&issuer, PLACEMENT_PROJECT);
    let created = client
        .create_database(
            "orders",
            &CreateDatabaseOptions {
                home_region: Some("gru".into()),
                replica_regions: Some(vec!["sjc".into()]),
                ..Default::default()
            },
        )
        .await
        .unwrap();
    assert_eq!(created.home_region, "gru");
    assert_eq!(created.replica_regions, ["sjc"]);
    assert!(!created.all_regions);
    let all = client
        .create_database(
            "orders",
            &CreateDatabaseOptions {
                home_region: Some("gru".into()),
                all_regions: true,
                ..Default::default()
            },
        )
        .await
        .unwrap();
    assert!(all.all_regions);
    let set = client
        .set_database_placement(
            PLACEMENT_DATABASE,
            &DatabasePlacement {
                replica_regions: Some(vec!["fra".into(), "sjc".into()]),
                ..Default::default()
            },
        )
        .await
        .unwrap();
    assert_eq!(set.replica_regions, ["fra", "sjc"]);
    let set = client
        .set_database_placement(
            PLACEMENT_DATABASE,
            &DatabasePlacement {
                all_regions: true,
                ..Default::default()
            },
        )
        .await
        .unwrap();
    assert!(set.all_regions);
    let moved = client
        .move_database_home(PLACEMENT_DATABASE, "sjc")
        .await
        .unwrap();
    assert_eq!(moved.home_region, "sjc");
    assert_eq!(moved.replica_regions, ["fra", "gru"]);
    assert_eq!(data.seen.lock().unwrap().len(), 6);
}

#[tokio::test]
async fn move_home_refusal_is_not_retried() {
    let data = replay(vec![placement("move-home-refused")]).await;
    let issuer = Fixture::new(|_| ok(grant("access", 900))).await;
    let client = data.project_client(&issuer, PLACEMENT_PROJECT);
    let err = client
        .move_database_home(PLACEMENT_DATABASE, "fra")
        .await
        .unwrap_err();
    assert_eq!(err.status(), Some(400));
    assert_eq!(err.code(), Some("invalid_request"));
    assert!(client
        .set_database_placement(PLACEMENT_DATABASE, &DatabasePlacement::default())
        .await
        .is_err());
    assert_eq!(data.seen.lock().unwrap().len(), 1);
}
