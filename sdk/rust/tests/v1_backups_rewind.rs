//! Point-in-time rewind wire parity (HTTP fixture).

#[path = "support/project_http.rs"]
mod http;
use http::{reply, Fixture, PROJECT};
use terrascale::v1::Client;

const REWOUND: &str = r#"{"id":"01ARZ3NDEKTSV4RRFFQ69G5FAW","projectId":"01ARZ3NDEKTSV4RRFFQ69G5FAV","name":"orders","branchedFrom":null,"createdAt":"2026-11-08T00:00:00Z","homeRegion":null,"replicaRegions":null,"placementVersion":3,"allRegions":false}"#;

#[tokio::test]
async fn rewind_point_in_time_posts_the_instant_and_destination() {
    let fixture = Fixture::new(vec![reply(200, REWOUND)]).await;
    let client = Client::builder(&fixture.url, "fixture-token")
        .project(PROJECT)
        .build()
        .unwrap();
    let rewound = client
        .database("orders")
        .rewind_point_in_time(&serde_json::json!({
            "pointInTime": "2026-11-07T23:59:00Z",
            "destination": "customer"
        }))
        .await
        .unwrap();
    let seen = fixture.requests.lock().unwrap();
    assert_eq!(seen.len(), 1);
    assert_eq!(seen[0].method, "POST");
    assert_eq!(
        seen[0].uri,
        format!("/v1/projects/{PROJECT}/databases/orders/point-in-time/rewind")
    );
    assert_eq!(
        seen[0].body,
        br#"{"destination":"customer","pointInTime":"2026-11-07T23:59:00Z"}"#.to_vec()
    );
    assert_eq!(rewound.name, "orders");
    assert_eq!(rewound.placement_version, 3);
    assert_eq!(rewound.home_region, None);
}
