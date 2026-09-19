//! ADR 0050 routing behavior: discovery, catalog fallback, product-origin
//! split, sticky regional pinning, 421 repair, and failover — exercised
//! through the public client surface against a mock transport.

use std::collections::VecDeque;
use std::sync::{Arc, Mutex};

use async_trait::async_trait;
use serde_json::{json, Value};
use terrascale::{
    ClientConfig, DatabaseScope, ErrorKind, HttpTransport, Query, RegionAffinity, RegionCode,
    Request, Response, Result, SubscribeOptions, TerraScaleClient, Transport,
};

#[derive(Debug)]
struct Mock {
    requests: Mutex<Vec<Request>>,
    responses: Mutex<VecDeque<Result<Response>>>,
}

impl Mock {
    fn new(responses: impl IntoIterator<Item = Result<Response>>) -> Arc<Self> {
        Arc::new(Self {
            requests: Mutex::new(Vec::new()),
            responses: Mutex::new(responses.into_iter().collect()),
        })
    }

    fn recorded(self: &Arc<Self>) -> Vec<Request> {
        self.requests.lock().expect("request mutex").clone()
    }
}

#[async_trait]
impl HttpTransport for Mock {
    async fn send(&self, request: Request) -> Result<Response> {
        self.requests.lock().expect("request mutex").push(request);
        // A nonzero round trip: measured-RTT tracking drops sub-millisecond
        // samples from the discovery header.
        tokio::time::sleep(std::time::Duration::from_millis(2)).await;
        self.responses
            .lock()
            .expect("response mutex")
            .pop_front()
            .unwrap_or_else(|| Err(terrascale::Error::transport("mock exhausted")))
    }
}

fn discovery_document(database: &str, endpoints: Value) -> Response {
    Response::json(
        200,
        &json!({
            "v": 1,
            "database": database,
            "branch": "main",
            "revision": 7,
            "updated": 1725300000,
            "residency": null,
            "placement": "known",
            "refreshSeconds": 300,
            "endpoints": endpoints,
        }),
    )
}

fn fra_gru_endpoints() -> Value {
    json!([
        { "region": "FRA", "url": "https://fra.api.terrascale.tech",
          "priority": 1, "capabilities": ["read", "write", "sql", "reactive"] },
        { "region": "GRU", "url": "https://gru.api.terrascale.tech",
          "priority": 2, "capabilities": ["read", "write", "sql", "reactive"] },
    ])
}

fn catalog() -> Response {
    Response::json(
        200,
        &json!({
            "regions": [
                {"code": "GRU", "name": "São Paulo", "country": "BR",
                 "latitude": -23.5, "longitude": -46.6, "available": true},
                {"code": "SFO", "name": "San Francisco", "country": "US",
                 "latitude": 37.8, "longitude": -122.4, "available": true},
            ],
            "hosts": {
                "GRU": "https://gru.api.terrascale.tech",
                "SFO": "https://sfo.api.terrascale.tech",
            },
        }),
    )
}

fn document_response() -> Response {
    Response::json(200, &json!({"name": "Ada"}))
}

fn production_client(mock: Arc<Mock>) -> TerraScaleClient {
    // The default configuration targets the public origin, so Atlas
    // discovery is active.
    TerraScaleClient::with_transport(
        ClientConfig::builder()
            .api_key("ts_test_key")
            .build()
            .expect("config"),
        mock,
    )
}

fn scope() -> DatabaseScope {
    DatabaseScope::new("orders")
}

#[tokio::test]
async fn database_requests_discover_once_then_pin_the_first_endpoint() {
    let mock = Mock::new([
        Ok(discovery_document("orders", fra_gru_endpoints())),
        Ok(document_response()),
        Ok(document_response()),
    ]);
    let sdk = production_client(Arc::clone(&mock));
    for _ in 0..2 {
        sdk.documents()
            .get(&scope(), "users", "doc-1")
            .await
            .expect("document");
    }
    let requests = mock.recorded();
    assert_eq!(requests.len(), 3, "the routing document is cached");
    let discovery = &requests[0];
    assert_eq!(
        discovery.url,
        "https://atlas.terrascale.tech/api/v1/discovery?database=orders&branch=main"
    );
    assert_eq!(discovery.method.as_str(), "GET");
    // Discovery is never authenticated (ADR 0050 §2).
    assert!(discovery
        .headers
        .iter()
        .all(|(name, _)| !name.eq_ignore_ascii_case("authorization")));
    for request in &requests[1..] {
        assert!(request
            .url
            .starts_with("https://fra.api.terrascale.tech/api/v1/databases/orders/"));
        assert!(request
            .headers
            .iter()
            .all(|(name, _)| !name.eq_ignore_ascii_case("x-terrascale-sdk-routing-database")));
    }
}

#[tokio::test]
async fn mutations_select_a_write_capable_endpoint() {
    let endpoints = json!([
        { "region": "FRA", "url": "https://fra.api.terrascale.tech",
          "priority": 1, "capabilities": ["read", "sql", "reactive"] },
        { "region": "GRU", "url": "https://gru.api.terrascale.tech",
          "priority": 2, "capabilities": ["read", "write", "sql", "reactive"] },
    ]);
    let mock = Mock::new([
        Ok(discovery_document("orders", endpoints)),
        Ok(document_response()),
        Ok(Response::json(200, &json!({"revision": "rev"}))),
    ]);
    let sdk = production_client(Arc::clone(&mock));
    sdk.documents()
        .get(&scope(), "users", "doc-1")
        .await
        .expect("read goes to the read-only replica");
    sdk.documents()
        .put(&scope(), "users", "doc-1", json!({"name": "Ada"}))
        .await
        .expect("write goes to the writer");
    let requests = mock.recorded();
    assert!(requests[1]
        .url
        .starts_with("https://fra.api.terrascale.tech/"));
    assert!(requests[2]
        .url
        .starts_with("https://gru.api.terrascale.tech/"));
}

#[tokio::test]
async fn discovery_failure_degrades_to_the_region_catalog() {
    let mock = Mock::new([
        Ok(Response::json(503, &json!({"error": {"code": "busy"}}))),
        Ok(catalog()),
        Ok(document_response()),
        Ok(document_response()),
    ]);
    let sdk = production_client(Arc::clone(&mock));
    for _ in 0..2 {
        sdk.documents()
            .get(&scope(), "users", "doc-1")
            .await
            .expect("document via catalog fallback");
    }
    let requests = mock.recorded();
    // One failed discovery, one catalog fetch (cached five minutes), two
    // direct regional requests. Never a proxy.
    assert_eq!(requests.len(), 4);
    assert_eq!(
        requests[1].url,
        "https://atlas.terrascale.tech/api/v1/regions"
    );
    for request in &requests[2..] {
        assert!(request.url.starts_with("https://gru.api.terrascale.tech/"));
    }
}

#[tokio::test]
async fn malformed_documents_degrade_to_the_region_catalog() {
    let hostile = Response {
        status: 200,
        headers: vec![("content-type".to_owned(), "application/json".to_owned())],
        body: serde_json::to_vec(&json!({
            "v": 1,
            "database": "orders",
            "branch": "main",
            "revision": 7,
            "placement": "known",
            "refreshSeconds": 300,
            "endpoints": [
                { "region": "FRA", "url": "https://attacker.example",
                  "priority": 1, "capabilities": ["read"] },
            ],
        }))
        .expect("hostile body")
        .into(),
    };
    let mock = Mock::new([Ok(hostile), Ok(catalog()), Ok(document_response())]);
    let sdk = production_client(Arc::clone(&mock));
    sdk.documents()
        .get(&scope(), "users", "doc-1")
        .await
        .expect("document via catalog fallback");
    let requests = mock.recorded();
    assert_eq!(
        requests[2].url,
        "https://gru.api.terrascale.tech/api/v1/databases/orders/collections/users/documents/doc-1"
    );
}

#[tokio::test]
async fn member_global_routes_use_the_catalog_and_the_catalog_route_hits_atlas() {
    let mock = Mock::new([
        Ok(catalog()),
        Ok(Response::json(200, &json!({"databases": []}))),
        Ok(catalog()),
    ]);
    let sdk = production_client(Arc::clone(&mock));
    // Database-less member route (ADR 0050 §8): catalog-selected endpoint.
    sdk.management()
        .databases()
        .list(None)
        .await
        .expect("database list");
    // `/api/v1/regions` exactly is the catalog itself, served by Atlas.
    sdk.management().regions().catalog().await.expect("catalog");
    let requests = mock.recorded();
    assert_eq!(
        requests[0].url,
        "https://atlas.terrascale.tech/api/v1/regions"
    );
    assert_eq!(
        requests[1].url,
        "https://gru.api.terrascale.tech/api/v1/databases"
    );
    // The public catalog route also targets the discovery origin.
    assert_eq!(
        requests[2].url,
        "https://atlas.terrascale.tech/api/v1/regions"
    );
}

#[tokio::test]
async fn total_routing_failure_is_a_typed_error_and_never_touches_the_product_origin() {
    let mock = Mock::new([
        Ok(Response::json(500, &json!({"error": {"code": "boom"}}))),
        Ok(Response::json(503, &json!({"error": {"code": "busy"}}))),
    ]);
    let sdk = production_client(Arc::clone(&mock));
    let error = sdk
        .documents()
        .get(&scope(), "users", "doc-1")
        .await
        .expect_err("no endpoint");
    assert_eq!(error.kind, ErrorKind::Unavailable);
    assert_eq!(error.code, "routing_unavailable");
    for request in mock.recorded() {
        assert!(request.url.starts_with("https://atlas.terrascale.tech/"));
    }
}

#[tokio::test]
async fn a_421_adopts_member_placement_for_the_retry() {
    let rejected = Response {
        status: 421,
        headers: vec![("x-terrascale-placement".to_owned(), "GRU".to_owned())],
        body: b"{}".to_vec().into(),
    };
    let mock = Mock::new([
        Ok(discovery_document(
            "orders",
            json!([
                { "region": "FRA", "url": "https://fra.api.terrascale.tech",
                  "priority": 1, "capabilities": ["read", "write", "sql", "reactive"] },
            ]),
        )),
        Ok(rejected),
        Ok(document_response()),
    ]);
    let sdk = production_client(Arc::clone(&mock));
    let error = sdk
        .documents()
        .get(&scope(), "users", "doc-1")
        .await
        .expect_err("421");
    assert_eq!(error.kind, ErrorKind::RegionRedirect);
    assert_eq!(error.placement.as_deref(), Some("GRU"));
    // The placement header teaches the scope; the retry goes straight to the
    // advertised region without a second discovery round trip.
    sdk.documents()
        .get(&scope(), "users", "doc-1")
        .await
        .expect("document on the advertised region");
    let requests = mock.recorded();
    assert_eq!(requests.len(), 3);
    assert!(requests[1]
        .url
        .starts_with("https://fra.api.terrascale.tech/"));
    assert!(requests[2]
        .url
        .starts_with("https://gru.api.terrascale.tech/"));
}

#[tokio::test]
async fn transport_failure_demotes_the_region_and_fails_over_in_document_order() {
    let mock = Mock::new([
        Ok(discovery_document("orders", fra_gru_endpoints())),
        Err(terrascale::Error::transport("regional connection failed")),
        Ok(document_response()),
    ]);
    let sdk = TerraScaleClient::with_transport(
        ClientConfig::builder()
            .api_key("ts_test_key")
            .max_retry_attempts(1)
            .retry_delay_ms(0)
            .build()
            .expect("config"),
        mock.clone(),
    );
    sdk.documents()
        .get(&scope(), "users", "doc-1")
        .await
        .expect("document from the next endpoint");
    let requests = mock.recorded();
    assert_eq!(requests.len(), 3);
    assert!(requests[1]
        .url
        .starts_with("https://fra.api.terrascale.tech/"));
    assert!(requests[2]
        .url
        .starts_with("https://gru.api.terrascale.tech/"));
}

#[tokio::test]
async fn concurrent_requests_for_one_scope_share_a_single_discovery() {
    let mock = Mock::new([
        Ok(discovery_document("orders", fra_gru_endpoints())),
        Ok(document_response()),
        Ok(document_response()),
    ]);
    let sdk = production_client(Arc::clone(&mock));
    let scope = scope();
    let documents = sdk.documents();
    let (first, second) = tokio::join!(
        documents.get(&scope, "users", "doc-1"),
        documents.get(&scope, "users", "doc-1"),
    );
    first.expect("document");
    second.expect("document");
    let requests = mock.recorded();
    assert_eq!(requests.len(), 3);
    assert_eq!(
        requests
            .iter()
            .filter(|request| request.url.contains("/api/v1/discovery"))
            .count(),
        1
    );
}

#[tokio::test]
async fn reactive_attach_routes_to_the_discovered_regional_endpoint() {
    let attach = Response::json(
        200,
        &json!({
            "view": "view-1",
            "generation": 4,
            "checkpoint": "Y3Ax",
            "snapshot_chunks": [],
            "log": "/api/v1/reactive/views/view-1/4/log?cut=1",
            "capability": "cap",
            "offset": 1,
        }),
    );
    let mock = Mock::new([
        Ok(discovery_document("orders", fra_gru_endpoints())),
        Ok(attach),
    ]);
    let sdk = production_client(Arc::clone(&mock));
    sdk.reactive()
        .subscribe(
            &scope(),
            &Query::raw("SELECT * FROM users".to_owned(), vec![]),
            SubscribeOptions::new().with_transport(Transport::LongPolling),
        )
        .await
        .expect("attach");
    let requests = mock.recorded();
    assert_eq!(requests.len(), 2);
    assert_eq!(
        requests[1].url,
        "https://fra.api.terrascale.tech/api/v1/reactive/views"
    );
}

#[tokio::test]
async fn successful_regional_requests_feed_measured_rtt_to_later_discovery() {
    let mock = Mock::new([
        Ok(discovery_document("orders", fra_gru_endpoints())),
        Ok(document_response()),
        Ok(discovery_document("billing", fra_gru_endpoints())),
        Ok(document_response()),
    ]);
    let sdk = production_client(Arc::clone(&mock));
    sdk.documents()
        .get(&scope(), "users", "doc-1")
        .await
        .expect("document");
    // A second scope discovers again; the cold fetch carried no measurement.
    sdk.documents()
        .get(&DatabaseScope::new("billing"), "users", "doc-1")
        .await
        .expect("document");
    let requests = mock.recorded();
    assert_eq!(requests.len(), 4);
    assert!(!requests[0]
        .headers
        .iter()
        .any(|(name, _)| name.eq_ignore_ascii_case("x-terrascale-region-rtt")));
    // The successful FRA round trip feeds the next discovery fetch (ADR 0050 §2).
    assert!(requests[1]
        .url
        .starts_with("https://fra.api.terrascale.tech/"));
    let rtt = requests[2]
        .headers
        .iter()
        .find(|(name, _)| name.eq_ignore_ascii_case("x-terrascale-region-rtt"))
        .map(|(_, value)| value.as_str())
        .expect("discovery carries the measured rtt header");
    let ms: u64 = rtt
        .strip_prefix("FRA=")
        .expect("FRA pair")
        .parse()
        .expect("milliseconds");
    assert!((1..=60_000).contains(&ms), "bounded pair, got {rtt}");
    // Member requests never see the header; a scope without a cached
    // document sends no revision.
    for request in &requests {
        if !request.url.starts_with("https://atlas.terrascale.tech/") {
            assert!(!request
                .headers
                .iter()
                .any(|(name, _)| name.eq_ignore_ascii_case("x-terrascale-region-rtt")));
        }
    }
    assert!(!requests[2].url.contains("rev="));
}

#[tokio::test]
async fn strict_affinity_never_discovers() {
    let mock = Mock::new([Ok(document_response())]);
    let sdk = TerraScaleClient::with_transport(
        ClientConfig::builder()
            .api_key("ts_test_key")
            .preferred_region(RegionCode::Gru)
            .region_affinity(RegionAffinity::Strict)
            .build()
            .expect("config"),
        mock.clone(),
    );
    sdk.documents()
        .get(&scope(), "users", "doc-1")
        .await
        .expect("direct regional request");
    let requests = mock.recorded();
    assert_eq!(requests.len(), 1);
    assert!(requests[0]
        .url
        .starts_with("https://gru.api.terrascale.tech/"));
}

#[tokio::test]
async fn an_atlas_override_forces_discovery_for_custom_origins() {
    let mock = Mock::new([
        Ok(discovery_document("orders", fra_gru_endpoints())),
        Ok(document_response()),
    ]);
    let sdk = TerraScaleClient::with_transport(
        ClientConfig::builder()
            .api_base_url("https://api.test")
            .atlas_base_url("https://atlas.test")
            .api_key("ts_test_key")
            .build()
            .expect("config"),
        mock.clone(),
    );
    sdk.documents()
        .get(&scope(), "users", "doc-1")
        .await
        .expect("document");
    let requests = mock.recorded();
    assert_eq!(
        requests[0].url,
        "https://atlas.test/api/v1/discovery?database=orders&branch=main"
    );
    assert!(requests[1]
        .url
        .starts_with("https://fra.api.terrascale.tech/"));
}
