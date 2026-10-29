#[path = "support/project_http.rs"]
mod http;
use http::{reply, Fixture, PROJECT};
use terrascale::v1::Client;

#[tokio::test]
async fn public_regions_are_dynamic_ordered_and_anonymous_for_both_client_scopes() {
    let fixture = Fixture::new(vec![
        reply(200, r#"{"regions":["fra","gru","new-9"]}"#),
        reply(200, r#"{"regions":[]}"#),
    ])
    .await;
    assert_eq!(
        fixture.client().list_regions().await.unwrap().regions,
        ["fra", "gru", "new-9"]
    );
    let platform = Client::for_platform(&fixture.url, "fixture-token")
        .build()
        .unwrap();
    assert!(platform.list_regions().await.unwrap().regions.is_empty());
    for request in fixture.requests.lock().unwrap().iter() {
        assert_eq!(request.uri, "/v1/regions");
        assert!(!request.headers.contains_key("authorization"));
        assert!(request.body.is_empty());
    }
}
#[tokio::test]
async fn prometheus_is_returned_verbatim_and_tenant_scoped() {
    let text = "# HELP tb_requests request rate\n# TYPE tb_requests gauge\ntb_requests{database=\"orders\",region=\"gru\"} 1.25\n";
    let fixture = Fixture::with_type(vec![reply(200, text)], "text/plain; version=0.0.4").await;
    assert_eq!(fixture.client().get_project_metrics().await.unwrap(), text);
    let seen = fixture.requests.lock().unwrap();
    assert_eq!(seen[0].uri, format!("/v1/projects/{PROJECT}/metrics"));
    assert_eq!(seen[0].headers["authorization"], "Bearer fixture-token");
}
#[tokio::test]
async fn metrics_refusals_and_wrong_status_are_not_text_successes() {
    let fixture = Fixture::new(vec![
        reply(403, r#"{"error":{"code":"forbidden_scope"}}"#),
        reply(204, ""),
        reply(200, "{}"),
    ])
    .await;
    assert_eq!(
        fixture
            .client()
            .get_project_metrics()
            .await
            .unwrap_err()
            .code(),
        Some("forbidden_scope")
    );
    assert!(fixture.client().get_project_metrics().await.is_err());
    assert!(fixture.client().list_regions().await.is_err());
    let platform = Client::for_platform(&fixture.url, "fixture-token")
        .build()
        .unwrap();
    assert!(platform.get_project_metrics().await.is_err());
    assert_eq!(fixture.requests.lock().unwrap().len(), 3);
}
