use super::*;
use crate::v1::{ChangeEvent, ChangesOptions};

#[test]
fn key_validation_and_debug_never_echo_credentials() {
    let builder = Client::for_project_api_key(PROJECT, key());
    assert!(!format!("{builder:?}").contains(&key()));
    let client = builder.build().unwrap();
    assert!(!format!("{client:?}").contains(&key()));
    let Auth::Key(auth) = &client.inner.auth else {
        unreachable!()
    };
    let (basic, endpoint) = (&auth.basic, &auth.endpoint);
    assert!(basic.is_sensitive());
    assert_eq!(
        endpoint.as_str(),
        "https://auth.terrascale.tech/terrascale/oauth/token"
    );
    for bad in [
        "".into(),
        "é".repeat(40),
        format!("{}!", key()),
        key().to_lowercase(),
        key().replace('_', ":"),
        format!("{PROJECT}_{}", "i".repeat(52)),
    ] {
        let error = Client::for_project_api_key(PROJECT, &bad)
            .build()
            .err()
            .unwrap();
        assert!(matches!(error, Error::InvalidInput(_)));
        assert!(!error.to_string().contains(PROJECT));
    }
    assert!(Client::for_project_api_key(PROJECT.to_lowercase(), key())
        .build()
        .is_err());
}

#[tokio::test]
async fn malformed_grants_are_typed_redacted_and_never_reach_the_data_plane() {
    let mut bodies = vec![
        "not-json".into(),
        "[]".into(),
        "{}".into(),
        "x".repeat(65 << 10),
    ];
    for expiry in [
        json!(0),
        json!(-1),
        json!(1.5),
        json!("Infinity"),
        json!(true),
        json!(u64::MAX),
    ] {
        bodies.push(
            json!({"access_token":"fixture-sensitive","token_type":"Bearer","expires_in":expiry})
                .to_string(),
        );
    }
    for token in ["", "=", "a=b", "a b", "é", "a\r\nx"] {
        bodies.push(grant(token, 900));
    }
    bodies.push(
        json!({"access_token":"fixture-sensitive","token_type":"Basic","expires_in":900})
            .to_string(),
    );
    for body in bodies {
        let auth = Fixture::new(move |_| ok(body.clone())).await;
        let data = Fixture::new(|_| panic!("data request after malformed grant")).await;
        let error = read(&data.client(&auth)).await.err().unwrap();
        assert!(matches!(
            error,
            Error::TokenExchange {
                status: Some(200),
                code: "invalid_token_response",
                ..
            }
        ));
        assert!(!error.is_uncertain());
        assert!(error.raw_body().is_none());
        assert!(!format!("{error:?} {error}").contains("fixture-sensitive"));
        assert!(data.seen.lock().unwrap().is_empty());
        assert_eq!(auth.seen.lock().unwrap().len(), 1);
    }
}

#[tokio::test]
async fn exchange_refusals_are_typed_redacted_and_retried_only_when_transient() {
    for status in [302, 400, 401, 403, 429, 502, 503, 504] {
        let auth = Fixture::new(move |_| {
            (
                status,
                "application/json",
                json!({
                    "error":"fixture-sensitive", "error_description": key()
                })
                .to_string(),
            )
        })
        .await;
        let data = Fixture::new(|_| panic!("data request after refused exchange")).await;
        let mut client = data.client(&auth);
        Arc::get_mut(&mut client.inner).unwrap().retry = RetryPolicy {
            base_delay: Duration::ZERO,
            ..RetryPolicy::default()
        };
        let error = client
            .send(Req::new(Method::POST, vec!["v1", "databases"]))
            .await
            .err()
            .unwrap();
        assert!(matches!(error, Error::TokenExchange { reason: None, .. }));
        assert_eq!(error.status(), Some(status));
        assert_eq!(
            error.code(),
            Some(if status == 401 {
                "invalid_key"
            } else {
                "token_exchange_refused"
            })
        );
        assert!(!error.is_uncertain());
        assert!(error.raw_body().is_none());
        assert!(!format!("{error:?} {error}").contains("fixture-sensitive"));
        assert!(!format!("{error:?} {error}").contains(&key()));
        assert!(data.seen.lock().unwrap().is_empty());
        let transient = matches!(status, 429 | 502 | 503 | 504);
        assert_eq!(
            auth.seen.lock().unwrap().len(),
            if transient { 4 } else { 1 }
        );
    }
}

#[tokio::test]
async fn exchange_recovers_from_a_transient_issuer_refusal_before_a_mutation() {
    let count = AtomicUsize::new(0);
    let auth = Fixture::new(move |_| match count.fetch_add(1, Ordering::SeqCst) {
        0 => (503, "text/html", "<html>busy</html>".into()),
        1 => (429, "application/json", "{}".into()),
        _ => ok(grant("access", 900)),
    })
    .await;
    let data = Fixture::new(|_| ok("{}".into())).await;
    let mut client = data.client(&auth);
    Arc::get_mut(&mut client.inner).unwrap().retry = RetryPolicy {
        base_delay: Duration::ZERO,
        ..RetryPolicy::default()
    };
    client
        .send(Req::new(Method::POST, vec!["v1", "databases"]))
        .await
        .unwrap();
    assert_eq!(auth.seen.lock().unwrap().len(), 3);
    let seen = data.seen.lock().unwrap();
    assert_eq!(seen.len(), 1);
    assert_eq!(seen[0].headers[AUTHORIZATION], "Bearer access");
}

#[tokio::test]
async fn exchange_refusals_name_the_oauth_error_and_issuer_request_id() {
    let auth = Fixture::new(|_| {
        (
            400,
            "application/json",
            json!({"error":"invalid_target","error_description":"fixture-sensitive"}).to_string(),
        )
    })
    .await;
    let data = Fixture::new(|_| panic!("data request after refused exchange")).await;
    let error = read(&data.client(&auth)).await.err().unwrap();
    assert_eq!(error.status(), Some(400));
    assert_eq!(error.code(), Some("token_exchange_refused"));
    assert_eq!(
        error.to_string(),
        "terrabase: token exchange: token_exchange_refused (HTTP 400): \
         invalid_target: the API key does not reach this project"
    );
    assert!(!format!("{error:?}").contains("fixture-sensitive"));
}

#[tokio::test]
async fn exchange_transport_failure_is_typed_and_does_not_send_mutation() {
    let auth = Fixture::new(|_| unreachable!()).await;
    let data = Fixture::new(|_| panic!("mutation sent without a token")).await;
    let client = data.client(&auth);
    drop(auth);
    tokio::task::yield_now().await;
    let error = client
        .send(Req::new(Method::POST, vec!["v1", "databases"]))
        .await
        .err()
        .unwrap();
    assert!(matches!(
        error,
        Error::TokenExchange {
            status: None,
            code: "token_transport_error",
            reason: Some("connection failed"),
            ..
        }
    ));
    assert!(!error.is_uncertain());
    assert!(data.seen.lock().unwrap().is_empty());
}

#[tokio::test]
async fn rejected_token_is_not_reused_after_failed_refresh() {
    let count = AtomicUsize::new(0);
    let auth = Fixture::new(move |_| match count.fetch_add(1, Ordering::SeqCst) {
        0 => ok(grant("old", 900)),
        1 => (503, "application/json", "{}".into()),
        _ => ok(grant("new", 900)),
    })
    .await;
    let data = Fixture::new(|r| {
        if r.headers[AUTHORIZATION] == "Bearer old" {
            (401, "application/json", "{}".into())
        } else {
            ok("{}".into())
        }
    })
    .await;
    let client = data.client(&auth);
    let error = read(&client).await.err().unwrap();
    assert_eq!(error.code(), Some("token_exchange_refused"));
    assert!(!error.is_uncertain());
    read(&client).await.unwrap();
    assert_eq!(auth.seen.lock().unwrap().len(), 3);
    assert_eq!(data.seen.lock().unwrap().len(), 2);
}

#[tokio::test]
async fn one_refresh_budget_survives_transient_read_retries() {
    let auth = Fixture::new(|_| ok(grant("access", 900))).await;
    let count = AtomicUsize::new(0);
    let data = Fixture::new(move |_| match count.fetch_add(1, Ordering::SeqCst) {
        1 => (
            503,
            "application/json",
            json!({"error":{"code":"capacity"}}).to_string(),
        ),
        _ => (401, "application/json", "{}".into()),
    })
    .await;
    let mut client = data.client(&auth);
    Arc::get_mut(&mut client.inner).unwrap().retry = RetryPolicy::default();
    assert_eq!(read(&client).await.err().unwrap().status(), Some(401));
    assert_eq!(auth.seen.lock().unwrap().len(), 2);
    assert_eq!(data.seen.lock().unwrap().len(), 3);
}

#[tokio::test]
async fn sse_reconnect_refreshes_after_401_and_preserves_last_delivered_cursor() {
    let count = AtomicUsize::new(0);
    let auth = Fixture::new(move |_| {
        ok(grant(
            if count.fetch_add(1, Ordering::SeqCst) == 0 {
                "old"
            } else {
                "new"
            },
            900,
        ))
    })
    .await;
    let count = AtomicUsize::new(0);
    let data = Fixture::new(move |r| {
        let call = count.fetch_add(1, Ordering::SeqCst);
        if call == 0 {
            assert_eq!(r.headers[AUTHORIZATION], "Bearer old");
            assert_eq!(r.headers["last-event-id"], "initial");
        } else {
            assert_eq!(r.headers["last-event-id"], "one");
        }
        if call == 1 {
            return (401, "application/json", "{}".into());
        }
        let cursor = if call == 0 { "one" } else { "two" };
        if call == 2 {
            assert_eq!(r.headers[AUTHORIZATION], "Bearer new");
        }
        (
            200,
            "text/event-stream",
            format!("id: {cursor}\nevent: cursor\ndata: {{\"cursor\":\"{cursor}\"}}\n\n"),
        )
    })
    .await;
    let client = data.client(&auth);
    let mut feed = client
        .database("orders")
        .changes(&ChangesOptions {
            last_event_id: Some("initial".into()),
            collections: vec!["items".into()],
            ..Default::default()
        })
        .await
        .unwrap();
    for expected in ["one", "two"] {
        let event = tokio::time::timeout(Duration::from_secs(3), feed.next())
            .await
            .unwrap()
            .unwrap()
            .unwrap();
        assert!(matches!(event, ChangeEvent::Cursor(cursor) if cursor == expected));
    }
    drop(feed);
    assert_eq!(auth.seen.lock().unwrap().len(), 2);
    let seen = data.seen.lock().unwrap();
    assert_eq!(seen.len(), 3);
    assert!(seen
        .iter()
        .all(|r| r.path
            == format!("/v1/projects/{PROJECT}/databases/orders/changes?collections=items")));
}

#[tokio::test]
async fn sse_reconnect_acquires_proactively_refreshed_token() {
    let count = AtomicUsize::new(0);
    let auth = Fixture::new(move |_| {
        ok(grant(
            if count.fetch_add(1, Ordering::SeqCst) == 0 {
                "old"
            } else {
                "new"
            },
            10,
        ))
    })
    .await;
    let data = Fixture::new(|_| {
        (
            200,
            "text/event-stream",
            "id: one\nevent: cursor\ndata: {\"cursor\":\"one\"}\n\n".into(),
        )
    })
    .await;
    let client = data.client(&auth);
    let mut feed = client
        .database("orders")
        .changes(&ChangesOptions::default())
        .await
        .unwrap();
    feed.next().await.unwrap().unwrap();
    advance(8).await;
    feed.next().await.unwrap().unwrap();
    drop(feed);
    assert_eq!(auth.seen.lock().unwrap().len(), 2);
    let seen = data.seen.lock().unwrap();
    assert_eq!(seen[0].headers[AUTHORIZATION], "Bearer old");
    assert_eq!(seen[1].headers[AUTHORIZATION], "Bearer new");
    assert_eq!(seen[1].headers["last-event-id"], "one");
}

#[tokio::test]
async fn sse_refusals_refresh_once_only_for_401() {
    for (status, attempts, exchanges) in [(401, 2, 2), (403, 1, 1)] {
        let auth = Fixture::new(|_| ok(grant("access", 900))).await;
        let data = Fixture::new(move |_| (status, "application/json", "{}".into())).await;
        let error = data
            .client(&auth)
            .database("orders")
            .change_bytes(&ChangesOptions::default())
            .await
            .err()
            .unwrap();
        assert_eq!(error.status(), Some(status));
        assert_eq!(auth.seen.lock().unwrap().len(), exchanges);
        assert_eq!(data.seen.lock().unwrap().len(), attempts);
    }
}

#[tokio::test]
async fn independent_projects_never_share_cached_grants_or_routes() {
    const OTHER: &str = "01ARZ3NDEKTSV4RRFFQ69G5FAY";
    let auth = Fixture::new(|r| {
        ok(grant(
            if String::from_utf8_lossy(&r.body).contains(OTHER) {
                "other"
            } else {
                "first"
            },
            900,
        ))
    })
    .await;
    let data = Fixture::new(|_| ok("{}".into())).await;
    let first = data.client(&auth);
    let second = data.project_client(&auth, OTHER);
    read(&first).await.unwrap();
    read(&second).await.unwrap();
    read(&first).await.unwrap();
    assert_eq!(auth.seen.lock().unwrap().len(), 2);
    let seen = data.seen.lock().unwrap();
    assert_eq!(seen[0].headers[AUTHORIZATION], "Bearer first");
    assert_eq!(seen[1].headers[AUTHORIZATION], "Bearer other");
    assert_eq!(seen[1].path, format!("/v1/projects/{OTHER}/databases"));
    assert_eq!(seen[2].headers[AUTHORIZATION], "Bearer first");
    assert_eq!(seen[2].path, format!("/v1/projects/{PROJECT}/databases"));
}

#[tokio::test]
async fn permanent_key_refusals_stop_exchanges_but_authority_outages_retry() {
    for code in ["key_revoked", "key_expired", "auth_unavailable"] {
        let auth = Fixture::new(|_| ok(grant("access-one", 900))).await;
        let calls = Arc::new(AtomicUsize::new(0));
        let count = calls.clone();
        let data = Fixture::new(move |_| {
            if count.fetch_add(1, Ordering::SeqCst) > 0 && code == "auth_unavailable" {
                return ok("{}".into());
            }
            (
                if code == "auth_unavailable" { 503 } else { 401 },
                "application/json",
                json!({"error":{"code":code,"message":"Replace the key."}}).to_string(),
            )
        })
        .await;
        let mut client = data.client(&auth);
        Arc::get_mut(&mut client.inner).unwrap().retry = RetryPolicy::default();
        if code == "auth_unavailable" {
            read(&client).await.unwrap();
            assert_eq!(calls.load(Ordering::SeqCst), 2);
        } else {
            for _ in 0..2 {
                let error = read(&client).await.err().unwrap();
                assert_eq!(error.code(), Some(code));
                assert!(error.to_string().contains("Replace the key."));
                let decoded = match &error {
                    Error::Response { error, .. } => error.as_ref(),
                    _ => &error,
                };
                assert!(matches!(
                    decoded,
                    Error::KeyRevoked { .. } | Error::KeyExpired { .. }
                ));
            }
            assert_eq!(calls.load(Ordering::SeqCst), 1);
        }
        assert_eq!(auth.seen.lock().unwrap().len(), 1);
    }
}

#[tokio::test]
async fn invalid_client_is_a_terminal_invalid_key_error() {
    let auth = Fixture::new(|_| {
        (
            401,
            "application/json",
            r#"{"error":"invalid_client"}"#.into(),
        )
    })
    .await;
    let data = Fixture::new(|_| panic!("data request after invalid key")).await;
    let client = data.client(&auth);
    for _ in 0..2 {
        assert!(matches!(
            read(&client).await,
            Err(Error::TokenExchange {
                code: "invalid_key",
                ..
            })
        ));
    }
    assert_eq!(auth.seen.lock().unwrap().len(), 1);
}
