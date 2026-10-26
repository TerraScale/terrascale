#[path = "support/project_http.rs"]
mod http;
use http::{reply, Fixture, PROJECT};
use serde_json::{json, Value};
use terrascale::v1::{Client, CollectionRules, Error};

const BUCKET: &str = "01ARZ3NDEKTSV4RRFFQ69G5FAC";
fn bucket() -> Value {
    json!({"id":BUCKET,"name":"avatars","region":"gru","rules":CollectionRules::default()})
}

#[tokio::test]
async fn bucket_lifecycle_uses_project_routes_wrapped_rules_and_after_pagination() {
    let bucket = bucket();
    let page = json!({"items":[bucket],"next":BUCKET});
    let fixture = Fixture::new(vec![
        reply(201, &bucket.to_string()),
        reply(200, &page.to_string()),
        reply(200, r#"{"items":[],"next":null}"#),
        reply(200, &bucket.to_string()),
        reply(204, ""),
    ])
    .await;
    let client = fixture.client();
    let rules = CollectionRules::default();
    assert_eq!(
        client
            .create_bucket("avatars", Some(&rules))
            .await
            .unwrap()
            .id,
        BUCKET
    );
    let first = client.list_buckets(None).await.unwrap();
    assert_eq!(first.items[0].rules, rules);
    let last = client.list_buckets(first.next.as_deref()).await.unwrap();
    assert!(last.items.is_empty());
    assert_eq!(last.next, None);
    client.set_bucket_rules(BUCKET, Some(&rules)).await.unwrap();
    client.delete_bucket(BUCKET).await.unwrap();
    let seen = fixture.requests.lock().unwrap();
    assert_eq!(seen.len(), 5);
    let root = format!("/v1/projects/{PROJECT}/buckets");
    for (index, method, suffix) in [
        (0, "POST", "".to_owned()),
        (1, "GET", "".to_owned()),
        (2, "GET", format!("?after={BUCKET}")),
        (3, "PUT", format!("/{BUCKET}/rules")),
        (4, "DELETE", format!("/{BUCKET}")),
    ] {
        assert_eq!(seen[index].method, method);
        assert_eq!(seen[index].uri, format!("{root}{suffix}"));
        assert_eq!(seen[index].headers["authorization"], "Bearer fixture-token");
    }
    assert_eq!(
        serde_json::from_slice::<Value>(&seen[0].body).unwrap(),
        json!({"name":"avatars","rules":rules})
    );
    assert_eq!(
        serde_json::from_slice::<Value>(&seen[3].body).unwrap(),
        json!({"rules":rules})
    );
}

#[tokio::test]
async fn null_bucket_rules_are_sent_explicitly_for_denying_end_users() {
    let body = bucket().to_string();
    let fixture = Fixture::new(vec![reply(201, &body), reply(200, &body)]).await;
    let client = fixture.client();
    client.create_bucket("avatars", None).await.unwrap();
    client.set_bucket_rules(BUCKET, None).await.unwrap();
    let seen = fixture.requests.lock().unwrap();
    assert_eq!(
        serde_json::from_slice::<Value>(&seen[0].body).unwrap(),
        json!({"name":"avatars","rules":null})
    );
    assert_eq!(
        serde_json::from_slice::<Value>(&seen[1].body).unwrap(),
        json!({"rules":null})
    );
}

#[tokio::test]
async fn invalid_ids_and_platform_clients_do_not_dispatch_bucket_calls() {
    let fixture = Fixture::new(vec![]).await;
    let client = fixture.client();
    for id in ["".into(), "../buckets".into(), BUCKET.to_lowercase()] {
        assert!(matches!(
            client.list_buckets(Some(&id)).await,
            Err(Error::InvalidInput(_))
        ));
        assert!(matches!(
            client.delete_bucket(&id).await,
            Err(Error::InvalidInput(_))
        ));
        assert!(matches!(
            client.set_bucket_rules(&id, None).await,
            Err(Error::InvalidInput(_))
        ));
    }
    let platform = Client::for_platform(&fixture.url, "fixture-token")
        .build()
        .unwrap();
    assert!(matches!(
        platform.list_buckets(None).await,
        Err(Error::InvalidInput(_))
    ));
    assert!(matches!(
        platform.create_bucket("avatars", None).await,
        Err(Error::InvalidInput(_))
    ));
    assert!(fixture.requests.lock().unwrap().is_empty());
}

#[tokio::test]
async fn malformed_bucket_metadata_and_pages_fail_instead_of_ending_pagination() {
    let mut invalid = bucket();
    invalid["id"] = json!(BUCKET.to_lowercase());
    let mut bodies = vec![
        json!({"items":[]}),
        json!({"items":[],"next":""}),
        json!({"items":[],"next":"not-ulid"}),
        json!({"items":[invalid],"next":null}),
        json!({"items":[{}],"next":null}),
        Value::Null,
    ];
    let mut invalid = bucket();
    invalid["rules"] = Value::Null;
    bodies.push(json!({"items":[invalid],"next":null}));
    for body in bodies {
        let fixture = Fixture::new(vec![reply(200, &body.to_string())]).await;
        let error = fixture.client().list_buckets(None).await.unwrap_err();
        assert_eq!(error.status(), Some(200));
        assert!(!error.is_uncertain());
    }
    let fixture = Fixture::new(vec![reply(201, "{}")]).await;
    assert!(fixture
        .client()
        .create_bucket("avatars", None)
        .await
        .unwrap_err()
        .is_uncertain());
}

#[tokio::test]
async fn bucket_mutation_refusals_keep_diagnostics_and_never_replay_uncertain_writes() {
    for (status, code) in [
        (401, "invalid_key"),
        (403, "forbidden_scope"),
        (404, "not_found"),
        (409, "conflict"),
        (503, "storage_busy"),
    ] {
        let body = json!({"error":{"code":code}}).to_string();
        let fixture = Fixture::new(vec![reply(status, &body)]).await;
        let client = Client::builder(&fixture.url, "fixture-token")
            .project(PROJECT)
            .build()
            .unwrap();
        let error = client.delete_bucket(BUCKET).await.unwrap_err();
        assert_eq!(error.status(), Some(status));
        assert_eq!(error.code(), Some(code));
        assert_eq!(error.raw_body(), Some(body.as_bytes()));
        assert_eq!(error.request_id(), Some("parity-fixture"));
        assert_eq!(error.is_uncertain(), status == 503);
        assert_eq!(fixture.requests.lock().unwrap().len(), 1);
    }
}
