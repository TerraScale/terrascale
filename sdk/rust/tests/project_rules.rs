#[path = "support/project_http.rs"]
mod http;
use http::{reply, Fixture, PROJECT};
use serde_json::{json, Value};
use terrascale::v1::{Client, CollectionRules, Error, ReadWriteGrants};

fn policy() -> CollectionRules {
    CollectionRules {
        owner: Some("/userId".into()),
        read: ReadWriteGrants {
            owner: true,
            public: false,
            roles: vec!["Editors".into()],
        },
        write: ReadWriteGrants {
            owner: true,
            public: false,
            roles: vec![],
        },
    }
}

#[tokio::test]
async fn rules_roundtrip_uses_project_path_and_direct_put_body() {
    let rules = policy();
    let body = json!({"rules":rules}).to_string();
    let fixture = Fixture::new(vec![
        reply(200, r#"{"rules":null}"#),
        reply(200, &body),
        reply(200, &body),
    ])
    .await;
    let collection = fixture.client().database("orders").collection("items");
    assert_eq!(collection.get_rules().await.unwrap(), None);
    assert_eq!(
        collection.put_rules(&rules).await.unwrap(),
        Some(rules.clone())
    );
    assert_eq!(collection.get_rules().await.unwrap(), Some(rules.clone()));
    let requests = fixture.requests.lock().unwrap();
    assert_eq!(requests.len(), 3);
    for request in requests.iter() {
        assert_eq!(
            request.uri,
            format!("/v1/projects/{PROJECT}/databases/orders/collections/items/rules")
        );
        assert_eq!(request.headers["authorization"], "Bearer fixture-token");
    }
    assert_eq!(requests[0].method, "GET");
    assert_eq!(requests[1].method, "PUT");
    assert_eq!(
        serde_json::from_slice::<Value>(&requests[1].body).unwrap(),
        json!(rules)
    );
}

#[tokio::test]
async fn explicit_deny_policy_preserves_null_owner_and_empty_roles() {
    let rules = CollectionRules::default();
    let body = json!({"rules":rules}).to_string();
    let fixture = Fixture::new(vec![reply(200, &body)]).await;
    fixture
        .client()
        .database("orders")
        .collection("items")
        .put_rules(&rules)
        .await
        .unwrap();
    assert_eq!(
        serde_json::from_slice::<Value>(&fixture.requests.lock().unwrap()[0].body).unwrap(),
        json!({"owner":null,"read":{"owner":false,"public":false,"roles":[]},"write":{"owner":false,"public":false,"roles":[]}})
    );
}

#[tokio::test]
async fn malformed_read_policies_do_not_become_undeclared_or_grant_access() {
    let mut bodies = vec![json!({}), Value::Null, json!({"rules":{}})];
    for member in ["read", "write"] {
        let mut body = json!({"rules":policy()});
        body["rules"][member]
            .as_object_mut()
            .unwrap()
            .remove("roles");
        bodies.push(body);
    }
    for body in bodies {
        let fixture = Fixture::new(vec![reply(200, &body.to_string())]).await;
        let error = fixture
            .client()
            .database("orders")
            .collection("items")
            .get_rules()
            .await
            .unwrap_err();
        assert_eq!(error.status(), Some(200));
        assert!(!error.is_uncertain());
    }
}

#[tokio::test]
async fn rules_refusals_and_uncertain_put_acknowledgements_stay_typed() {
    for (status, code) in [
        (401, "invalid_key"),
        (403, "forbidden_scope"),
        (404, "unknown_collection"),
        (503, "commit_uncertain"),
    ] {
        let body = json!({"error":{"code":code}}).to_string();
        let fixture = Fixture::new(vec![reply(status, &body)]).await;
        let client = Client::builder(&fixture.url, "fixture-token")
            .project(PROJECT)
            .build()
            .unwrap();
        let error = client
            .database("orders")
            .collection("items")
            .put_rules(&policy())
            .await
            .unwrap_err();
        assert_eq!(error.status(), Some(status));
        assert_eq!(error.code(), Some(code));
        assert_eq!(error.raw_body(), Some(body.as_bytes()));
        assert_eq!(error.is_uncertain(), status == 503);
        assert_eq!(fixture.requests.lock().unwrap().len(), 1);
    }
    let fixture = Fixture::new(vec![reply(200, "{}")]).await;
    let error = fixture
        .client()
        .database("orders")
        .collection("items")
        .put_rules(&policy())
        .await
        .unwrap_err();
    assert!(error.is_uncertain());
}

#[tokio::test]
async fn invalid_selectors_and_platform_clients_do_not_dispatch_rules_calls() {
    let fixture = Fixture::new(vec![]).await;
    for (database, collection) in [("../orders", "items"), ("orders", "items/users")] {
        let collection = fixture.client().database(database).collection(collection);
        assert!(matches!(
            collection.get_rules().await,
            Err(Error::InvalidInput(_))
        ));
        assert!(matches!(
            collection.put_rules(&policy()).await,
            Err(Error::InvalidInput(_))
        ));
    }
    let platform = Client::for_platform(&fixture.url, "fixture-token")
        .build()
        .unwrap();
    assert!(matches!(
        platform
            .database("orders")
            .collection("items")
            .get_rules()
            .await,
        Err(Error::InvalidInput(_))
    ));
    assert!(fixture.requests.lock().unwrap().is_empty());
}

#[tokio::test]
async fn nullable_response_grants_deny_and_serialize_as_full_put_policy() {
    let fixture = Fixture::new(vec![
        reply(200, r#"{"rules":{"owner":null,"read":null,"write":{"owner":true,"public":false,"roles":null}}}"#),
        reply(200, r#"{"rules":null}"#),
    ]).await;
    let collection = fixture.client().database("orders").collection("items");
    let rules = collection.get_rules().await.unwrap().unwrap();
    assert_eq!(rules.read, ReadWriteGrants::default());
    assert!(rules.write.owner && !rules.write.public && rules.write.roles.is_empty());
    collection.put_rules(&rules).await.unwrap();
    assert_eq!(
        serde_json::from_slice::<Value>(&fixture.requests.lock().unwrap()[1].body).unwrap(),
        json!({"owner":null,"read":{"owner":false,"public":false,"roles":[]},"write":{"owner":true,"public":false,"roles":[]}})
    );
    let rules: CollectionRules =
        serde_json::from_value(json!({"owner":null,"read":null,"write":null})).unwrap();
    assert_eq!(rules, CollectionRules::default());
}
