use super::*;

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
