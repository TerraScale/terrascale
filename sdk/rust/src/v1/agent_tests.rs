use super::*;
use crate::v1::{CreateDatabaseOptions, ForkDatabaseOptions, ScopedKeyAccess, ScopedKeyOptions};
use serde_json::Value;

fn lifecycle() -> Vec<Value> {
    serde_json::from_str(include_str!(
        "../../contracts/terrabase/agent/lifecycle.json"
    ))
    .unwrap()
}

async fn recorded(exchanges: &[Value]) -> Fixture {
    let exchanges = exchanges.to_vec();
    let next = AtomicUsize::new(0);
    Fixture::new(move |request| {
        let exchange = &exchanges[next.fetch_add(1, Ordering::SeqCst)];
        assert_eq!(request.method, exchange["request"]["method"]);
        assert_eq!(request.path, exchange["request"]["path"]);
        let body = if request.body.is_empty() {
            Value::Null
        } else {
            serde_json::from_slice(&request.body).unwrap()
        };
        assert_eq!(body, exchange["request"]["body"]);
        let response = &exchange["response"];
        (
            response["status"].as_u64().unwrap() as u16,
            "application/json",
            if response["body"].is_null() {
                String::new()
            } else {
                response["body"].to_string()
            },
        )
    })
    .await
}

fn scoped_client(data: &Fixture, issuer: &Fixture) -> Client {
    let mut client = data.client(issuer);
    let Auth::Key(key) = &mut Arc::get_mut(&mut client.inner).unwrap().auth else {
        unreachable!()
    };
    Arc::get_mut(key).unwrap().scoped_endpoint =
        format!("{}/v1/terrascale/api-keys/self/scoped-keys", issuer.url);
    Arc::get_mut(&mut client.inner).unwrap().retry = RetryPolicy::default();
    client
}

#[tokio::test]
async fn agent_database_lifecycle_matches_recorded_contract() {
    let exchanges = lifecycle();
    let data = recorded(&exchanges[..9]).await;
    let issuer = Fixture::new(|_| ok(grant("access", 900))).await;
    let scoped = recorded(&exchanges[9..]).await;
    let client = scoped_client(&data, &issuer);
    let ttl = CreateDatabaseOptions {
        ttl_seconds: Some(3600),
        ..Default::default()
    };
    let created = client.create_database("agent", &ttl).await.unwrap();
    assert_eq!(
        serde_json::to_value(&created).unwrap()["expiresAt"],
        exchanges[0]["response"]["body"]["expiresAt"]
    );
    assert!(client
        .create_database("durable", &CreateDatabaseOptions::default())
        .await
        .unwrap()
        .expires_at
        .is_none());
    let forked = client
        .fork_database("agent", "fork", &ForkDatabaseOptions::default())
        .await
        .unwrap();
    assert!(forked.expires_at.is_none());
    assert_eq!(forked.branched_from.as_deref(), Some(created.id.as_str()));
    let past = client
        .fork_database(
            &created.id,
            "past",
            &ForkDatabaseOptions {
                point_in_time: Some("2026-11-04T12:30:00Z".into()),
                ttl_seconds: Some(3600),
            },
        )
        .await
        .unwrap();
    assert_eq!(past.expires_at, created.expires_at);
    assert_eq!(
        client
            .set_database_ttl(&created.id, 3600)
            .await
            .unwrap()
            .expires_at,
        created.expires_at
    );
    assert!(client
        .clear_database_ttl("agent")
        .await
        .unwrap()
        .expires_at
        .is_none());
    client.delete_database(&created.id).await.unwrap();
    for request in data.seen.lock().unwrap().iter() {
        assert_eq!(request.headers[AUTHORIZATION], "Bearer access");
    }
    let client = scoped_client(&data, &scoped);
    for (index, access) in [ScopedKeyAccess::Read, ScopedKeyAccess::ReadWrite]
        .into_iter()
        .enumerate()
    {
        let result = client
            .create_scoped_key(&ScopedKeyOptions {
                project: Some(PROJECT.into()),
                database: created.id.clone(),
                access,
                ttl_seconds: if index == 0 { None } else { Some(3600) },
            })
            .await
            .unwrap();
        assert_eq!(result.database, created.id);
        assert_eq!(result.access, access);
        assert_eq!(result.expires_at, "2026-11-04T13:00:00Z");
        assert!(!format!("{result:?}").contains(&result.key));
        Client::for_project_api_key(PROJECT, result.key)
            .build()
            .unwrap();
    }
    let seen = scoped.seen.lock().unwrap();
    assert_eq!(seen.len(), 2);
    for request in seen.iter() {
        assert_eq!(
            request.headers[AUTHORIZATION],
            format!("Basic {}", STANDARD.encode(format!("{KEY_ID}:{}", key())))
        );
    }
    assert_eq!(data.seen.lock().unwrap().len(), 9);
    assert_eq!(issuer.seen.lock().unwrap().len(), 1);
}

#[tokio::test]
async fn agent_database_refusals_preserve_errors_without_replaying_mutations() {
    let exchanges: Vec<Value> = serde_json::from_str(include_str!(
        "../../contracts/terrabase/agent/refusals.json"
    ))
    .unwrap();
    let data = recorded(&exchanges[..1]).await;
    let issuer = Fixture::new(|_| ok(grant("access", 900))).await;
    let scoped = recorded(&exchanges[1..]).await;
    let client = scoped_client(&data, &scoped);
    let error = client
        .create_scoped_key(&ScopedKeyOptions {
            project: None,
            database: "agent".into(),
            access: ScopedKeyAccess::Read,
            ttl_seconds: None,
        })
        .await
        .unwrap_err();
    assert!(matches!(error, Error::InvalidInput(_)));
    let client = data.client(&issuer);
    let error = client
        .create_database("denied", &CreateDatabaseOptions::default())
        .await
        .unwrap_err();
    assert_eq!(error.status(), Some(403));
    assert_eq!(error.code(), Some("forbidden_scope"));
    let client = scoped_client(&data, &scoped);
    let options = ScopedKeyOptions {
        project: None,
        database: "01ARZ3NDEKTSV4RRFFQ69G5FAW".into(),
        access: ScopedKeyAccess::Read,
        ttl_seconds: None,
    };
    for (status, code) in [
        (403, "forbidden"),
        (401, "invalid_client"),
        (400, "invalid_request"),
        (429, "rate_limited"),
    ] {
        let error = client.create_scoped_key(&options).await.unwrap_err();
        assert_eq!(error.status(), Some(status));
        assert_eq!(error.code(), Some(code));
        assert!(!format!("{error:?}").contains(&key()));
        if status == 429 {
            let Error::Response { error, .. } = error else {
                unreachable!()
            };
            assert!(
                matches!(*error, Error::RateLimited { retry_after: Some(delay) } if delay == Duration::from_secs(7))
            );
        }
    }
    for ttl in [59, 31_536_001] {
        assert!(matches!(
            client.set_database_ttl("agent", ttl).await,
            Err(Error::InvalidInput(_))
        ));
    }
    let mut invalid = options;
    invalid.ttl_seconds = Some(2_592_001);
    assert!(matches!(
        client.create_scoped_key(&invalid).await,
        Err(Error::InvalidInput(_))
    ));
    assert!(matches!(
        Client::for_project(PROJECT, "token")
            .build()
            .unwrap()
            .create_scoped_key(&ScopedKeyOptions {
                ttl_seconds: None,
                ..invalid
            })
            .await,
        Err(Error::InvalidInput(_))
    ));
    assert_eq!(data.seen.lock().unwrap().len(), 1);
    assert_eq!(scoped.seen.lock().unwrap().len(), 4);
}
