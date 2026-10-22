#![allow(deprecated)] // Exercises the native API kept until v1 replaces it.
use async_trait::async_trait;
use serde_json::{json, Value as Json};
use std::{
    collections::VecDeque,
    sync::{Arc, Mutex},
    time::Duration,
};
use terrascale::*;

const SUCCESS: &[u8] = include_bytes!("../contracts/atlas/native-discovery-success.json");
const FORBIDDEN: &[u8] = include_bytes!("../contracts/atlas/native-discovery-forbidden.json");
const FIXED_MILLIS: u64 = 1_790_769_600_000;
fn scope() -> DatabaseScope {
    DatabaseScope::new(
        "10000000-0000-0000-0000-000000000001".parse().unwrap(),
        "20000000-0000-0000-0000-000000000001".parse().unwrap(),
        u64::MAX,
    )
}
fn native_key() -> String {
    use base64::{engine::general_purpose::URL_SAFE_NO_PAD, Engine};
    format!(
        "tbk1.{}.{}",
        URL_SAFE_NO_PAD.encode([1; 16]),
        URL_SAFE_NO_PAD.encode([2; 32])
    )
}
fn config() -> ClientConfig {
    ClientConfig::builder()
        .api_key(native_key())
        .auth_api_key("isolated-auth-secret")
        .build()
        .unwrap()
}
fn fixture() -> Json {
    serde_json::from_slice(SUCCESS).unwrap()
}
fn raw(status: u16, body: &[u8]) -> Response {
    Response {
        status,
        headers: vec![
            ("cache-control".into(), "private, no-store".into()),
            ("content-type".into(), "application/json".into()),
        ],
        body: bytes::Bytes::copy_from_slice(body),
    }
}
#[derive(Debug)]
struct Clock(Mutex<DiscoveryTime>);
impl Clock {
    fn new() -> Arc<Self> {
        Arc::new(Self(Mutex::new(DiscoveryTime {
            unix_millis: FIXED_MILLIS,
            monotonic: Duration::ZERO,
        })))
    }
    fn advance(&self, millis: u64) {
        let mut time = self.0.lock().unwrap();
        time.unix_millis += millis;
        time.monotonic += Duration::from_millis(millis);
    }
}
impl DiscoveryClock for Clock {
    fn now(&self) -> Result<DiscoveryTime> {
        Ok(*self.0.lock().unwrap())
    }
}
#[derive(Debug, Default)]
struct Mock {
    requests: Mutex<Vec<Request>>,
    replies: Mutex<VecDeque<(Result<Response>, u64)>>,
    clock: Option<Arc<Clock>>,
}
impl Mock {
    fn new(clock: Arc<Clock>) -> Arc<Self> {
        Arc::new(Self {
            clock: Some(clock),
            ..Self::default()
        })
    }
    fn response(&self, status: u16, body: &Json) {
        self.replies
            .lock()
            .unwrap()
            .push_back((Ok(Response::json(status, body)), 0));
    }
    fn raw(&self, status: u16, body: &[u8], advance: u64) {
        self.replies
            .lock()
            .unwrap()
            .push_back((Ok(raw(status, body)), advance));
    }
}
#[async_trait]
impl HttpTransport for Mock {
    async fn send(&self, request: Request) -> Result<Response> {
        self.requests.lock().unwrap().push(request);
        tokio::task::yield_now().await;
        let (response, advance) = self
            .replies
            .lock()
            .unwrap()
            .pop_front()
            .expect("unexpected transport call");
        if let Some(clock) = &self.clock {
            clock.advance(advance);
        } else if advance > 0 {
            tokio::time::sleep(Duration::from_millis(advance)).await;
        }
        response
    }
}
fn fixture_router() -> (AtlasDiscovery, Arc<Mock>, Arc<Clock>) {
    let clock = Clock::new();
    let mock = Mock::new(clock.clone());
    (
        AtlasDiscovery::with_clock(config(), mock.clone(), clock.clone()),
        mock,
        clock,
    )
}

#[tokio::test]
async fn derived_receiver_codec_bytes_preserve_full_u64_and_fixed_clock_provenance() {
    let (router, mock, _) = fixture_router();
    mock.raw(200, SUCCESS, 100);
    let resolved = router
        .resolve(&scope(), NativeRequirement::Point)
        .await
        .unwrap();
    assert_eq!(resolved.endpoint.url, "https://gru.api.terrascale.tech");
    assert_eq!(
        resolved.endpoint.catalog_digest,
        Digest::from_bytes([0xcc; 32])
    );
    assert_eq!(resolved.metadata.authority.publisher_generation.0, u64::MAX);
    assert_eq!(resolved.metadata.revision, DecimalU64(41));
    assert_eq!(resolved.metadata.refresh_seconds, 8);
    assert_eq!(resolved.metadata.residency, None);
    let requests = mock.requests.lock().unwrap();
    assert_eq!(requests.len(), 1);
    assert_eq!(requests[0].url, "https://atlas.terrascale.tech/api/v1/discovery?tenant_id=10000000-0000-0000-0000-000000000001&database_id=20000000-0000-0000-0000-000000000001&shard_id=18446744073709551615");
    assert_eq!(requests[0].method, Method::Get);
    assert_eq!(
        requests[0]
            .headers
            .iter()
            .filter(|(name, _)| name.eq_ignore_ascii_case("authorization"))
            .count(),
        1
    );
    assert!(requests[0]
        .headers
        .contains(&("authorization".into(), format!("Bearer {}", native_key()))));
    assert!(!format!("{:?}", requests[0]).contains(&native_key()));
    assert!(!requests[0]
        .headers
        .iter()
        .any(|(_, value)| value.contains("isolated-auth-secret")));
}
#[tokio::test]
async fn actual_forbidden_response_is_typed_and_not_retried() {
    let (router, mock, _) = fixture_router();
    mock.raw(403, FORBIDDEN, 100);
    let error = router
        .resolve(&scope(), NativeRequirement::Point)
        .await
        .unwrap_err();
    assert_eq!(error.status, Some(403));
    assert_eq!(error.kind, ErrorKind::Authorization);
    assert_eq!(error.code, "discovery_forbidden");
    assert!(error.fix.is_some());
    assert_eq!(mock.requests.lock().unwrap().len(), 1);
}
#[tokio::test]
async fn credentials_custom_origin_nil_scope_and_zero_shard_fail_before_transport() {
    let mock = Arc::new(Mock::default());
    for credential in [
        None,
        Some("legacy".into()),
        Some(format!("{}.extra", native_key())),
        Some(native_key().replace("tbk1", "TBK1")),
        Some(format!("{}=", native_key())),
        Some(format!("tbk1.{}.{}", "A".repeat(21), "A".repeat(43))),
    ] {
        let mut builder = ClientConfig::builder();
        if let Some(key) = credential {
            builder = builder.api_key(key);
        }
        let router = AtlasDiscovery::new(builder.build().unwrap(), mock.clone());
        assert_eq!(
            router
                .resolve(&scope(), NativeRequirement::Point)
                .await
                .unwrap_err()
                .code,
            "invalid_discovery_credential"
        );
    }
    for origin in [
        "https://custom.invalid",
        "https://api.terrascale.tech",
        "https://atlas.terrascale.tech/custom",
        "https://atlas.terrascale.tech:444",
    ] {
        let router = AtlasDiscovery::new(
            ClientConfig::builder()
                .atlas_base_url(origin)
                .api_key(native_key())
                .build()
                .unwrap(),
            mock.clone(),
        );
        assert_eq!(
            router
                .resolve(&scope(), NativeRequirement::Point)
                .await
                .unwrap_err()
                .code,
            "invalid_native_discovery_origin"
        );
    }
    assert_eq!(
        ClientConfig::builder()
            .atlas_base_url("http://atlas.terrascale.tech")
            .api_key(native_key())
            .build()
            .unwrap_err()
            .code,
        "insecure_base_url"
    );
    let router = AtlasDiscovery::new(config(), mock.clone());
    let mut target = scope();
    target.shard = DecimalU64(0);
    assert_eq!(
        router
            .resolve(&target, NativeRequirement::Point)
            .await
            .unwrap_err()
            .code,
        "invalid_discovery_scope"
    );
    target = scope();
    target.tenant = "00000000-0000-0000-0000-000000000000".parse().unwrap();
    assert_eq!(
        router
            .resolve(&target, NativeRequirement::Point)
            .await
            .unwrap_err()
            .code,
        "invalid_discovery_scope"
    );
    assert!(mock.requests.lock().unwrap().is_empty());
}
#[test]
fn structured_rtt_hints_are_bounded_unique_and_do_not_use_static_catalog() {
    assert!(RegionRtt::new("NEW-REGION-9", 100).is_ok());
    for (region, rtt) in [
        ("gru", 1),
        ("API", 1),
        ("ATLAS", 1),
        ("GRU", 0),
        ("GRU", 60001),
        ("-GRU", 1),
        ("ABCDEFGHIJKLMNOPQ", 1),
    ] {
        assert!(RegionRtt::new(region, rtt).is_err());
    }
    let hint = RegionRtt::new("NEW-REGION-9", 60000).unwrap();
    assert!(ClientConfig::builder()
        .region_rtt(vec![hint.clone(), hint])
        .build()
        .is_err());
    assert!(ClientConfig::builder()
        .region_rtt(
            (0..21)
                .map(|i| RegionRtt::new(format!("R{i}"), 1).unwrap())
                .collect()
        )
        .build()
        .is_err());
}
#[tokio::test]
async fn receiver_ranking_dynamic_regions_residency_and_operation_capabilities_are_respected() {
    let clock = Clock::new();
    let mock = Mock::new(clock.clone());
    let config = ClientConfig::builder()
        .api_key(native_key())
        .region_rtt(vec![
            RegionRtt::new("Z9-NEW", 3).unwrap(),
            RegionRtt::new("GRU", 100).unwrap(),
        ])
        .build()
        .unwrap();
    let router = AtlasDiscovery::with_clock(config, mock.clone(), clock);
    let mut body = fixture();
    let mut first = body["endpoints"][0].clone();
    first["region"] = json!("Z9-NEW");
    first["url"] = json!("https://z9-new.api.terrascale.tech");
    first["capabilities"]["query"] = json!(false);
    first["capabilities"]["subscriptions"] = json!(false);
    first["capabilities"]["write"] = json!(false);
    body["endpoints"][0]["priority"] = json!(2);
    body["endpoints"] = json!([first, body["endpoints"][0].clone()]);
    body["residency"] = json!(["Z9-NEW", "GRU"]);
    mock.response(200, &body);
    assert_eq!(
        router
            .resolve(&scope(), NativeRequirement::Point)
            .await
            .unwrap()
            .endpoint
            .region,
        "Z9-NEW"
    );
    for requirement in [
        NativeRequirement::Query,
        NativeRequirement::Subscription,
        NativeRequirement::TransactionStatus,
        NativeRequirement::Commit {
            conditional: false,
            atomic: false,
        },
    ] {
        assert_eq!(
            router
                .resolve(&scope(), requirement)
                .await
                .unwrap()
                .endpoint
                .region,
            "GRU"
        );
    }
    // Selecting a less highly ranked endpoint for a different capability must
    // not pin later reads away from the first admitted endpoint.
    assert_eq!(
        router
            .resolve(&scope(), NativeRequirement::Point)
            .await
            .unwrap()
            .endpoint
            .region,
        "Z9-NEW"
    );
    assert_eq!(mock.requests.lock().unwrap().len(), 1);
    for requirement in [
        NativeRequirement::Commit {
            conditional: true,
            atomic: false,
        },
        NativeRequirement::Commit {
            conditional: false,
            atomic: true,
        },
    ] {
        assert_eq!(
            router
                .resolve(&scope(), requirement)
                .await
                .unwrap_err()
                .code,
            "native_capability_unavailable"
        );
    }
    let requests = mock.requests.lock().unwrap();
    assert_eq!(requests.len(), 1);
    assert!(requests[0]
        .headers
        .contains(&("x-terrascale-region-rtt".into(), "Z9-NEW=3,GRU=100".into())));
}

#[tokio::test]
async fn closed_schema_nullable_fields_duplicates_and_invalid_native_metadata_fail() {
    let mut mutations = Vec::new();
    for path in [
        "/residency",
        "/endpoints/0/security_writer",
        "/endpoints/0/capabilities/read",
    ] {
        let mut body = fixture();
        let (parent, name) = path.rsplit_once('/').unwrap();
        body.pointer_mut(parent)
            .unwrap()
            .as_object_mut()
            .unwrap()
            .remove(name);
        mutations.push(body);
    }
    for (path, value) in [
        ("/schema_version", json!(2)),
        ("/extra", json!(true)),
        (
            "/scope/tenant_id",
            json!("30000000-0000-0000-0000-000000000001"),
        ),
        ("/scope/shard_id", json!("018446744073709551615")),
        (
            "/authority/publisher_generation",
            json!(18446744073709551615u64),
        ),
        ("/authority/publisher_generation", json!("0")),
        ("/authority/cell", json!("GRU")),
        ("/authority/deployment_id", json!("/evil")),
        ("/authority/publisher_id", json!("a_b")),
        ("/revision", json!("0")),
        ("/revision", json!("18446744073709551616")),
        ("/published_at", json!(0)),
        ("/published_at", json!(253402300800u64)),
        ("/published_at", json!(1790769598u64)),
        ("/expires_at", json!(253402300800u64)),
        ("/published_at", json!(1790769601)),
        ("/published_at", json!(1790769600.0)),
        ("/expires_at", json!(1790769600)),
        ("/expires_at", json!(9007199254740992u64)),
        ("/refreshSeconds", json!(0)),
        ("/refreshSeconds", json!(11)),
        ("/residency", json!([])),
        ("/residency", json!(["GRU", "GRU"])),
        ("/residency", json!(["UNKNOWN"])),
        ("/endpoints/0/security_writer", json!({})),
        ("/endpoints/0/priority", json!(0)),
        ("/endpoints/0/catalog/digest", json!("CC".repeat(32))),
        ("/endpoints/0/capabilities/client_v1", json!(false)),
        (
            "/endpoints/0/capabilities/conditional_mutation",
            json!(true),
        ),
        ("/endpoints/0/capabilities/atomic_groups", json!(true)),
        ("/endpoints/0/capabilities/security_state", json!(true)),
        ("/endpoints/0/region", json!("API")),
        ("/endpoints/0/region", json!("gru")),
    ] {
        let mut body = fixture();
        if path == "/extra" {
            body["extra"] = value;
        } else {
            *body.pointer_mut(path).unwrap() = value;
        }
        mutations.push(body);
    }
    for url in [
        "https://api.terrascale.tech",
        "https://gru.terrascale.tech",
        "https://evil.invalid",
        "https://gru.api.terrascale.tech/",
        "https://gru.api.terrascale.tech:443",
        "https://GRU.api.terrascale.tech",
        "http://gru.api.terrascale.tech",
        "https://gru.api.terrascale.tech?x=1",
        "https://key@gru.api.terrascale.tech",
        "https://gru.api.terrascale.tech.",
    ] {
        let mut body = fixture();
        body["endpoints"][0]["url"] = json!(url);
        mutations.push(body);
    }
    let mut duplicate = fixture();
    duplicate["endpoints"] = json!([
        duplicate["endpoints"][0].clone(),
        duplicate["endpoints"][0].clone()
    ]);
    duplicate["endpoints"][1]["priority"] = json!(2);
    mutations.push(duplicate);
    let mut empty = fixture();
    empty["endpoints"] = json!([]);
    mutations.push(empty);
    for body in mutations {
        let (router, mock, _) = fixture_router();
        mock.response(200, &body);
        assert_eq!(
            router
                .resolve(&scope(), NativeRequirement::Point)
                .await
                .unwrap_err()
                .kind,
            ErrorKind::InvalidResponse,
            "{body}"
        );
    }
    for text in [
        String::from_utf8(SUCCESS.to_vec()).unwrap().replacen(
            "\"revision\":\"41\"",
            "\"revision\":\"41\",\"revision\":\"42\"",
            1,
        ),
        String::from_utf8(SUCCESS.to_vec()).unwrap().replacen(
            "\"read\":true",
            "\"read\":true,\"read\":false",
            1,
        ),
    ] {
        let (router, mock, _) = fixture_router();
        mock.raw(200, text.as_bytes(), 0);
        assert_eq!(
            router
                .resolve(&scope(), NativeRequirement::Point)
                .await
                .unwrap_err()
                .kind,
            ErrorKind::InvalidResponse
        );
    }
    for status in [201, 202, 299, 301, 307, 401, 500] {
        let (router, mock, _) = fixture_router();
        mock.raw(status, SUCCESS, 0);
        assert_eq!(
            router
                .resolve(&scope(), NativeRequirement::Point)
                .await
                .unwrap_err()
                .status,
            Some(status)
        );
        assert_eq!(mock.requests.lock().unwrap().len(), 1);
    }
}
#[tokio::test]
async fn ttl_clamps_down_and_consumed_lease_and_backward_clocks_fail_closed() {
    let (router, mock, clock) = fixture_router();
    mock.raw(200, SUCCESS, 2_100);
    assert_eq!(
        router
            .resolve(&scope(), NativeRequirement::Point)
            .await
            .unwrap()
            .metadata
            .refresh_seconds,
        6
    );
    clock.advance(4_000);
    let mut fresh = fixture();
    fresh["published_at"] = json!(1790769606u64);
    fresh["expires_at"] = json!(1790769615u64);
    fresh["revision"] = json!("42");
    mock.response(200, &fresh);
    assert_eq!(
        router
            .resolve(&scope(), NativeRequirement::Point)
            .await
            .unwrap()
            .metadata
            .revision,
        DecimalU64(42)
    );
    clock.0.lock().unwrap().unix_millis -= 1;
    assert_eq!(
        router
            .resolve(&scope(), NativeRequirement::Point)
            .await
            .unwrap_err()
            .code,
        "discovery_clock_invalid"
    );
    let (router, mock, _) = fixture_router();
    mock.raw(200, SUCCESS, 5_100);
    assert_eq!(
        router
            .resolve(&scope(), NativeRequirement::Point)
            .await
            .unwrap_err()
            .code,
        "discovery_expired"
    );
    let (router, mock, _) = fixture_router();
    mock.raw(200, SUCCESS, 8_001);
    assert_eq!(
        router
            .resolve(&scope(), NativeRequirement::Point)
            .await
            .unwrap_err()
            .code,
        "discovery_expired"
    );
}
#[tokio::test]
async fn observation_floors_survive_expiry_and_reject_cross_cell_deployment_or_regression() {
    for (path, value) in [
        ("/revision", json!("40")),
        ("/authority/publisher_generation", json!("4")),
        ("/authority/cell", json!("iad")),
        ("/authority/deployment_id", json!("other")),
    ] {
        let (router, mock, clock) = fixture_router();
        let mut body = fixture();
        body["authority"]["publisher_generation"] = json!("5");
        mock.response(200, &body);
        router
            .resolve(&scope(), NativeRequirement::Point)
            .await
            .unwrap();
        clock.advance(8_000);
        body["published_at"] = json!(1790769608u64);
        body["expires_at"] = json!(1790769618u64);
        *body.pointer_mut(path).unwrap() = value;
        mock.response(200, &body);
        assert_eq!(
            router
                .resolve(&scope(), NativeRequirement::Point)
                .await
                .unwrap_err()
                .code,
            "routing_observation_regressed"
        );
        let mut fresh = fixture();
        fresh["authority"]["publisher_generation"] = json!("6");
        fresh["authority"]["publisher_id"] = json!("publisher-b");
        fresh["revision"] = json!("42");
        fresh["published_at"] = json!(1790769608u64);
        fresh["expires_at"] = json!(1790769618u64);
        mock.response(200, &fresh);
        router
            .resolve(&scope(), NativeRequirement::Point)
            .await
            .unwrap();
        let requests = mock.requests.lock().unwrap();
        assert_eq!(requests.len(), 3);
        for request in &requests[1..] {
            let url = reqwest::Url::parse(&request.url).unwrap();
            let pairs = url
                .query_pairs()
                .collect::<std::collections::HashMap<_, _>>();
            assert_eq!(pairs["rev"], "41");
            assert_eq!(pairs["observed_publisher_generation"], "5");
            assert_eq!(pairs["observed_cell"], "gru");
            assert_eq!(pairs["observed_deployment_id"], "fixture-deployment");
        }
    }
}
#[tokio::test]
async fn concurrent_scope_resolution_publishes_once_and_capacity_never_evicts_floors() {
    let (router, mock, _) = fixture_router();
    mock.raw(200, SUCCESS, 100);
    let mut tasks = Vec::new();
    for _ in 0..32 {
        let router = router.clone();
        tasks.push(tokio::spawn(async move {
            router.resolve(&scope(), NativeRequirement::Point).await
        }));
    }
    for task in tasks {
        assert_eq!(
            task.await.unwrap().unwrap().metadata.revision,
            DecimalU64(41)
        );
    }
    assert_eq!(mock.requests.lock().unwrap().len(), 1);
    for n in 2..=128 {
        let mut target = scope();
        target.database = format!("20000000-0000-0000-0000-{n:012x}").parse().unwrap();
        let mut body = fixture();
        body["scope"]["database_id"] = json!(target.database);
        mock.response(200, &body);
        router
            .resolve(&target, NativeRequirement::Point)
            .await
            .unwrap();
    }
    let mut target = scope();
    target.database = "30000000-0000-0000-0000-000000000001".parse().unwrap();
    assert_eq!(
        router
            .resolve(&target, NativeRequirement::Point)
            .await
            .unwrap_err()
            .code,
        "discovery_scope_capacity"
    );
    assert_eq!(
        router
            .resolve(&scope(), NativeRequirement::Point)
            .await
            .unwrap()
            .metadata
            .revision,
        DecimalU64(41)
    );
    assert_eq!(mock.requests.lock().unwrap().len(), 128);
}
