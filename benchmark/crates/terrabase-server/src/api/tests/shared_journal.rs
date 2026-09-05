use super::*;

#[tokio::test]
async fn pending_http_views_share_dispatch_and_cancel_the_last_worker() {
    use std::time::Duration;
    let app = router(state_with_keys_and_control());
    let key = "Bearer tskey_test_abcdefghijklmnop";
    let response = app
        .clone()
        .oneshot(
            Request::post("/api/v1/databases")
                .header("authorization", key)
                .body(Body::from(r#"{"name":"prod","placement":["GRU"],"residency":null}"#))
                .unwrap(),
        )
        .await
        .unwrap();
    assert_eq!(response.status(), StatusCode::CREATED);
    let mut attached = Vec::new();
    for total in [1, 2] {
        let response = app.clone().oneshot(Request::post("/api/v1/reactive/views")
            .header("authorization", key).body(Body::from(serde_json::json!({
                "databaseId": "prod", "statement": format!("SELECT total FROM orders WHERE total = {total} LIMIT 10")
            }).to_string())).unwrap()).await.unwrap();
        assert_eq!(response.status(), StatusCode::CREATED);
        attached.push(
            serde_json::from_slice::<serde_json::Value>(
                &axum::body::to_bytes(response.into_body(), 65_536).await.unwrap(),
            )
            .unwrap(),
        );
    }
    tokio::time::sleep(Duration::from_millis(110)).await;
    let mut polls = Vec::new();
    for view in &attached {
        let app = app.clone();
        let request = Request::get(view["log"].as_str().unwrap())
            .header("authorization", key)
            .header("x-terrascale-view-capability", view["capability"].as_str().unwrap())
            .body(Body::empty())
            .unwrap();
        polls.push(tokio::spawn(async move { app.oneshot(request).await.unwrap() }));
    }
    let view = attached[0]["view"].as_str().unwrap();
    tokio::time::timeout(Duration::from_secs(2), async {
        while crate::reactive_api::source_dispatch_stats(view).0 != 2 {
            tokio::task::yield_now().await;
        }
    })
    .await
    .unwrap();
    assert_eq!(crate::reactive_api::source_dispatch_stats(view), (2, 2, 1, true));
    let response = app
        .clone()
        .oneshot(
            Request::put("/api/v1/databases/prod/collections/orders/documents/one")
                .header("authorization", key)
                .body(Body::from(r#"{"total":1}"#))
                .unwrap(),
        )
        .await
        .unwrap();
    assert_eq!(response.status(), StatusCode::OK);
    let matching = polls.remove(0);
    let response = tokio::time::timeout(Duration::from_secs(2), matching).await.unwrap().unwrap();
    assert_eq!(response.status(), StatusCode::OK);
    let bytes = axum::body::to_bytes(response.into_body(), 65_536).await.unwrap();
    assert!(matches!(
        terrabase_sync_core::wire::decode_envelope(&bytes).unwrap().message,
        terrabase_sync_core::wire::Message::Delta { .. }
    ));
    let unrelated = polls.remove(0);
    assert!(!unrelated.is_finished());
    unrelated.abort();
    assert!(unrelated.await.unwrap_err().is_cancelled());
    assert_eq!(crate::reactive_api::source_dispatch_stats(view), (0, 0, 1, false));
}

#[tokio::test]
async fn distinct_sql_views_share_one_journal_pass() {
    let app = router(state_with_keys_and_control());
    let key = "Bearer tskey_test_abcdefghijklmnop";
    let response = app
        .clone()
        .oneshot(
            Request::post("/api/v1/databases")
                .header("authorization", key)
                .body(Body::from(r#"{"name":"prod","placement":["GRU"],"residency":null}"#))
                .unwrap(),
        )
        .await
        .unwrap();
    assert_eq!(response.status(), StatusCode::CREATED);
    let mut attached = Vec::new();
    for minimum in 0..8 {
        let response = app.clone().oneshot(Request::post("/api/v1/reactive/views")
            .header("authorization", key)
            .body(Body::from(serde_json::json!({ "databaseId": "prod",
                "statement": format!("SELECT total FROM orders WHERE total > {minimum} LIMIT 10")
            }).to_string())).unwrap()).await.unwrap();
        assert_eq!(response.status(), StatusCode::CREATED);
        let body = axum::body::to_bytes(response.into_body(), 65_536).await.unwrap();
        attached.push(serde_json::from_slice::<serde_json::Value>(&body).unwrap());
    }
    let (source, reads) = crate::reactive_api::source_stats(attached[0]["view"].as_str().unwrap());
    assert_eq!(reads, 0);
    for view in &attached {
        assert_eq!(crate::reactive_api::source_stats(view["view"].as_str().unwrap()).0, source);
    }
    let response = app
        .clone()
        .oneshot(
            Request::put("/api/v1/databases/prod/collections/orders/documents/one")
                .header("authorization", key)
                .body(Body::from(r#"{"total":99}"#))
                .unwrap(),
        )
        .await
        .unwrap();
    assert_eq!(response.status(), StatusCode::OK);
    tokio::time::sleep(std::time::Duration::from_millis(110)).await;
    let mut tasks = Vec::new();
    for view in &attached {
        let app = app.clone();
        let request = Request::get(view["log"].as_str().unwrap())
            .header("authorization", key)
            .header("x-terrascale-view-capability", view["capability"].as_str().unwrap())
            .body(Body::empty())
            .unwrap();
        tasks.push(tokio::spawn(async move {
            let response = app.oneshot(request).await.unwrap();
            assert_eq!(response.status(), StatusCode::OK);
            let bytes = axum::body::to_bytes(response.into_body(), 65_536).await.unwrap();
            let envelope = terrabase_sync_core::wire::decode_envelope(&bytes).unwrap();
            let terrabase_sync_core::wire::Message::Delta { to_revision, .. } = envelope.message
            else {
                panic!("delta")
            };
            to_revision
        }));
    }
    let mut cuts = Vec::new();
    for task in tasks {
        cuts.push(task.await.unwrap());
    }
    for view in &attached {
        assert_eq!(
            crate::reactive_api::source_stats(view["view"].as_str().unwrap()),
            (source, 1),
            "eight distinct SQL views replay one source page, rather than one page each"
        );
    }
    assert_leaving_row(app, &attached, &cuts).await;
}

async fn assert_leaving_row(app: axum::Router, attached: &[serde_json::Value], cuts: &[u64]) {
    use terrabase_sync_core::wire::{Message, ResultOperation};
    let key = "Bearer tskey_test_abcdefghijklmnop";
    let response = app
        .clone()
        .oneshot(
            Request::put("/api/v1/databases/prod/collections/orders/documents/one")
                .header("authorization", key)
                .body(Body::from(r#"{"total":-1}"#))
                .unwrap(),
        )
        .await
        .unwrap();
    assert_eq!(response.status(), StatusCode::OK);
    tokio::time::sleep(std::time::Duration::from_millis(110)).await;
    for (view, cut) in attached.iter().zip(cuts) {
        let url = format!(
            "/api/v1/reactive/views/{}/{}/log?offset={cut}",
            view["view"].as_str().unwrap(),
            view["generation"]
        );
        let response = app
            .clone()
            .oneshot(
                Request::get(url)
                    .header("authorization", key)
                    .header("x-terrascale-view-capability", view["capability"].as_str().unwrap())
                    .body(Body::empty())
                    .unwrap(),
            )
            .await
            .unwrap();
        assert_eq!(response.status(), StatusCode::OK);
        let bytes = axum::body::to_bytes(response.into_body(), 65_536).await.unwrap();
        let Message::Delta { operations, .. } =
            terrabase_sync_core::wire::decode_envelope(&bytes).unwrap().message
        else {
            panic!("delta")
        };
        assert_eq!(
            operations,
            vec![ResultOperation::Remove { identity: "one".into() }],
            "all eight views remove a row that no longer matches their intervals"
        );
    }
}

#[tokio::test]
async fn irrelevant_rows_do_not_reexecute_unbounded_sql_views() {
    let state = state_with_keys_and_control();
    let app = router(Arc::clone(&state));
    let key = "Bearer tskey_test_abcdefghijklmnop";
    let response = app
        .clone()
        .oneshot(
            Request::post("/api/v1/databases")
                .header("authorization", key)
                .body(Body::from(r#"{"name":"prod","placement":["GRU"],"residency":null}"#))
                .unwrap(),
        )
        .await
        .unwrap();
    assert_eq!(response.status(), StatusCode::CREATED);
    let attach = || {
        Request::post("/api/v1/reactive/views").header("authorization", key)
        .body(Body::from(r#"{"databaseId":"prod","statement":"SELECT total FROM orders WHERE total > 1000"}"#)).unwrap()
    };
    assert_eq!(app.clone().oneshot(attach()).await.unwrap().status(), StatusCode::CREATED);
    let host = super::super::document_host(&state, "prod").unwrap();
    let tenant = terrabase::types::TenantId::try_new(&state.scope.0).unwrap();
    let before = host.engine().queries().recent_index_selections(&tenant).len();
    assert!(before > 0);
    let response = app
        .clone()
        .oneshot(
            Request::put("/api/v1/databases/prod/collections/orders/documents/one")
                .header("authorization", key)
                .body(Body::from(r#"{"total":99}"#))
                .unwrap(),
        )
        .await
        .unwrap();
    assert_eq!(response.status(), StatusCode::OK);
    tokio::time::sleep(std::time::Duration::from_millis(110)).await;
    assert_eq!(app.oneshot(attach()).await.unwrap().status(), StatusCode::CREATED);
    assert_eq!(
        host.engine().queries().recent_index_selections(&tenant).len(),
        before,
        "an irrelevant row must not run fallback snapshot SQL"
    );
}
