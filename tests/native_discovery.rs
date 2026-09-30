use async_trait::async_trait;
use serde_json::{json, Value as Json};
use std::{
    collections::VecDeque,
    sync::{
        atomic::{AtomicUsize, Ordering},
        Arc, Mutex,
    },
    time::{Duration, SystemTime, UNIX_EPOCH},
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

#[derive(Debug)]
enum LifecycleReply {
    Pending,
    Ready(Result<Response>),
}
#[derive(Debug, Default)]
struct LifecycleTransport {
    requests: Mutex<Vec<Request>>,
    replies: Mutex<VecDeque<LifecycleReply>>,
    dispatched: tokio::sync::Notify,
    active: AtomicUsize,
    released: AtomicUsize,
}
impl LifecycleTransport {
    fn pending(&self) {
        self.replies
            .lock()
            .unwrap()
            .push_back(LifecycleReply::Pending);
    }
    fn reply(&self, response: Result<Response>) {
        self.replies
            .lock()
            .unwrap()
            .push_back(LifecycleReply::Ready(response));
    }
    fn admit(&self, target: &DatabaseScope) {
        let mut body = fixture();
        body["scope"]["database_id"] = json!(target.database);
        self.reply(Ok(Response::json(200, &body)));
    }
}
struct LifecycleSend<'a>(&'a LifecycleTransport);
impl Drop for LifecycleSend<'_> {
    fn drop(&mut self) {
        self.0.active.fetch_sub(1, Ordering::SeqCst);
        self.0.released.fetch_add(1, Ordering::SeqCst);
    }
}
#[async_trait]
impl HttpTransport for LifecycleTransport {
    async fn send(&self, request: Request) -> Result<Response> {
        self.requests.lock().unwrap().push(request);
        self.active.fetch_add(1, Ordering::SeqCst);
        let _send = LifecycleSend(self);
        let reply = self
            .replies
            .lock()
            .unwrap()
            .pop_front()
            .expect("unexpected lifecycle transport call");
        match reply {
            LifecycleReply::Pending => {
                self.dispatched.notify_one();
                std::future::pending().await
            }
            LifecycleReply::Ready(response) => response,
        }
    }
}
fn lifecycle_router() -> (AtlasDiscovery, Arc<LifecycleTransport>, Arc<Clock>) {
    let clock = Clock::new();
    let transport = Arc::new(LifecycleTransport::default());
    (
        AtlasDiscovery::with_clock(config(), transport.clone(), clock.clone()),
        transport,
        clock,
    )
}
fn lifecycle_scope(number: u16) -> DatabaseScope {
    let mut target = scope();
    target.database = format!("20000000-0000-0000-0000-{number:012x}")
        .parse()
        .unwrap();
    target
}

async fn lifecycle_dispatch<F: std::future::Future>(
    operation: std::pin::Pin<&mut F>,
    transport: &LifecycleTransport,
) {
    let requests = transport.requests.lock().unwrap().len();
    let active = transport.active.load(Ordering::SeqCst);
    // Pending at an earlier cooperative yield is not proof of transport
    // dispatch. The send acknowledges only after consuming its pending reply.
    tokio::select! {
        _ = operation => panic!("lifecycle operation completed before pending send dispatch"),
        () = transport.dispatched.notified() => {}
    }
    assert_eq!(transport.requests.lock().unwrap().len(), requests + 1);
    assert_eq!(transport.active.load(Ordering::SeqCst), active + 1);
}

async fn lifecycle_queue<F: std::future::Future>(
    operation: std::pin::Pin<&mut F>,
    transport: &LifecycleTransport,
) {
    let requests = transport.requests.lock().unwrap().len();
    // Give this poll a fresh cooperative budget so Pending represents the
    // held scope mutex rather than yielding before the waiter is queued.
    tokio::task::yield_now().await;
    assert!(futures_util::poll!(operation).is_pending());
    assert_eq!(transport.requests.lock().unwrap().len(), requests);
}

#[tokio::test]
async fn actual_worker_sqlite_bytes_preserve_full_u64_and_fixed_clock_provenance() {
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
                .discovery_base_url(origin)
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
            .discovery_base_url("http://atlas.terrascale.tech")
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

#[tokio::test]
async fn lifecycle_failed_initial_admissions_release_empty_scope_capacity() {
    let (router, transport, _) = lifecycle_router();
    for number in 1..=128 {
        let expected = match number % 3 {
            0 => {
                transport.reply(Err(Error::transport("lifecycle fixture failure")));
                ErrorKind::Transport
            }
            1 => {
                transport.reply(Ok(raw(403, FORBIDDEN)));
                ErrorKind::Authorization
            }
            _ => {
                transport.reply(Ok(Response::json(200, &json!({"invalid": true}))));
                ErrorKind::InvalidResponse
            }
        };
        assert_eq!(
            router
                .resolve(&lifecycle_scope(number), NativeRequirement::Point)
                .await
                .unwrap_err()
                .kind,
            expected
        );
    }
    let target = lifecycle_scope(129);
    transport.admit(&target);
    assert_eq!(
        router
            .resolve(&target, NativeRequirement::Point)
            .await
            .unwrap()
            .metadata
            .revision,
        DecimalU64(41)
    );
    assert_eq!(transport.requests.lock().unwrap().len(), 129);
    assert_eq!(transport.active.load(Ordering::SeqCst), 0);
    assert_eq!(transport.released.load(Ordering::SeqCst), 129);
}

#[tokio::test]
async fn lifecycle_cancelled_admissions_release_all_reservations_without_bypassing_capacity() {
    let (router, transport, _) = lifecycle_router();
    let targets = (1..=128).map(lifecycle_scope).collect::<Vec<_>>();
    let mut pending = Vec::new();
    for target in &targets {
        transport.pending();
        let mut request = Box::pin(router.resolve(target, NativeRequirement::Point));
        lifecycle_dispatch(request.as_mut(), &transport).await;
        pending.push(request);
    }
    let overflow = lifecycle_scope(129);
    assert_eq!(
        router
            .resolve(&overflow, NativeRequirement::Point)
            .await
            .unwrap_err()
            .code,
        "discovery_scope_capacity"
    );
    assert_eq!(transport.requests.lock().unwrap().len(), 128);
    assert_eq!(transport.active.load(Ordering::SeqCst), 128);
    drop(pending);
    assert_eq!(transport.active.load(Ordering::SeqCst), 0);
    assert_eq!(transport.released.load(Ordering::SeqCst), 128);

    // All canceled reservations must be reusable, while successful floors still
    // consume exactly the original capacity.
    for number in 201..=328 {
        let target = lifecycle_scope(number);
        transport.admit(&target);
        router
            .resolve(&target, NativeRequirement::Point)
            .await
            .unwrap();
    }
    assert_eq!(
        router
            .resolve(&overflow, NativeRequirement::Point)
            .await
            .unwrap_err()
            .code,
        "discovery_scope_capacity"
    );
    assert_eq!(transport.requests.lock().unwrap().len(), 256);
    assert_eq!(transport.active.load(Ordering::SeqCst), 0);
    assert_eq!(transport.released.load(Ordering::SeqCst), 256);
}

#[tokio::test(start_paused = true)]
async fn lifecycle_timed_out_admissions_release_transport_and_scope_capacity() {
    let (router, transport, _) = lifecycle_router();
    for number in 1..=128 {
        let target = lifecycle_scope(number);
        transport.pending();
        let mut request = Box::pin(router.resolve(&target, NativeRequirement::Point));
        lifecycle_dispatch(request.as_mut(), &transport).await;
        tokio::time::advance(Duration::from_secs(30)).await;
        let error = request.await.unwrap_err();
        assert_eq!(error.kind, ErrorKind::Transport);
        assert_eq!(error.message, "Atlas request deadline exceeded.");
        assert_eq!(transport.active.load(Ordering::SeqCst), 0);
    }
    let target = lifecycle_scope(129);
    transport.admit(&target);
    router
        .resolve(&target, NativeRequirement::Point)
        .await
        .unwrap();
    assert_eq!(transport.requests.lock().unwrap().len(), 129);
    assert_eq!(transport.active.load(Ordering::SeqCst), 0);
    assert_eq!(transport.released.load(Ordering::SeqCst), 129);
}

#[tokio::test]
async fn lifecycle_cancelled_owner_and_waiter_preserve_remaining_singleflight_reservation() {
    let (router, transport, _) = lifecycle_router();
    let target = lifecycle_scope(1);
    transport.pending();
    let mut owner = Box::pin(router.resolve(&target, NativeRequirement::Point));
    lifecycle_dispatch(owner.as_mut(), &transport).await;
    let mut canceled_waiter = Box::pin(router.resolve(&target, NativeRequirement::Point));
    lifecycle_queue(canceled_waiter.as_mut(), &transport).await;

    let others = (2..=128).map(lifecycle_scope).collect::<Vec<_>>();
    let mut pending = Vec::new();
    for other in &others {
        transport.pending();
        let mut request = Box::pin(router.resolve(other, NativeRequirement::Point));
        lifecycle_dispatch(request.as_mut(), &transport).await;
        pending.push(request);
    }
    let overflow = lifecycle_scope(129);
    drop(canceled_waiter);
    assert_eq!(
        router
            .resolve(&overflow, NativeRequirement::Point)
            .await
            .unwrap_err()
            .code,
        "discovery_scope_capacity"
    );
    assert_eq!(transport.released.load(Ordering::SeqCst), 0);

    let mut waiter = Box::pin(router.resolve(&target, NativeRequirement::Point));
    lifecycle_queue(waiter.as_mut(), &transport).await;
    drop(owner);
    assert_eq!(transport.released.load(Ordering::SeqCst), 1);
    assert_eq!(
        router
            .resolve(&overflow, NativeRequirement::Point)
            .await
            .unwrap_err()
            .code,
        "discovery_scope_capacity"
    );
    let mut newcomer = Box::pin(router.resolve(&target, NativeRequirement::Point));
    lifecycle_queue(newcomer.as_mut(), &transport).await;
    assert_eq!(transport.requests.lock().unwrap().len(), 128);
    transport.admit(&target);
    assert_eq!(waiter.await.unwrap().metadata.revision, DecimalU64(41));
    assert_eq!(newcomer.await.unwrap().metadata.revision, DecimalU64(41));
    assert_eq!(transport.requests.lock().unwrap().len(), 129);
    drop(pending);
    assert_eq!(transport.active.load(Ordering::SeqCst), 0);
    assert_eq!(transport.released.load(Ordering::SeqCst), 129);

    // If the surviving queued waiter also leaves, exactly that unobserved
    // reservation must be reclaimed while the other 127 sends remain active.
    let (router, transport, _) = lifecycle_router();
    transport.pending();
    let mut owner = Box::pin(router.resolve(&target, NativeRequirement::Point));
    lifecycle_dispatch(owner.as_mut(), &transport).await;
    let mut last_waiter = Box::pin(router.resolve(&target, NativeRequirement::Point));
    lifecycle_queue(last_waiter.as_mut(), &transport).await;
    let mut pending = Vec::new();
    for other in &others {
        transport.pending();
        let mut request = Box::pin(router.resolve(other, NativeRequirement::Point));
        lifecycle_dispatch(request.as_mut(), &transport).await;
        pending.push(request);
    }
    drop(owner);
    assert_eq!(
        router
            .resolve(&overflow, NativeRequirement::Point)
            .await
            .unwrap_err()
            .code,
        "discovery_scope_capacity"
    );
    drop(last_waiter);
    assert_eq!(transport.active.load(Ordering::SeqCst), 127);
    assert_eq!(transport.released.load(Ordering::SeqCst), 1);
    transport.admit(&overflow);
    router
        .resolve(&overflow, NativeRequirement::Point)
        .await
        .unwrap();
    assert_eq!(
        router
            .resolve(&lifecycle_scope(130), NativeRequirement::Point)
            .await
            .unwrap_err()
            .code,
        "discovery_scope_capacity"
    );
    assert_eq!(transport.requests.lock().unwrap().len(), 129);
    drop(pending);
    assert_eq!(transport.active.load(Ordering::SeqCst), 0);
    assert_eq!(transport.released.load(Ordering::SeqCst), 129);
}

#[tokio::test]
async fn lifecycle_cancelled_and_failed_refreshes_preserve_observation_floor_and_capacity() {
    let (router, transport, clock) = lifecycle_router();
    for number in 1..=128 {
        let target = lifecycle_scope(number);
        transport.admit(&target);
        router
            .resolve(&target, NativeRequirement::Point)
            .await
            .unwrap();
    }
    clock.advance(8_000);
    let target = lifecycle_scope(1);
    transport.pending();
    let mut refresh = Box::pin(router.resolve(&target, NativeRequirement::Point));
    lifecycle_dispatch(refresh.as_mut(), &transport).await;
    drop(refresh);
    transport.reply(Err(Error::transport("refresh fixture failure")));
    let error = router
        .resolve(&target, NativeRequirement::Point)
        .await
        .unwrap_err();
    assert_eq!(error.kind, ErrorKind::Transport);
    assert_eq!(error.message, "refresh fixture failure");
    let mut regressed = fixture();
    regressed["published_at"] = json!(1790769608u64);
    regressed["expires_at"] = json!(1790769617u64);
    regressed["revision"] = json!("40");
    transport.reply(Ok(Response::json(200, &regressed)));
    assert_eq!(
        router
            .resolve(&target, NativeRequirement::Point)
            .await
            .unwrap_err()
            .code,
        "routing_observation_regressed"
    );
    {
        let requests = transport.requests.lock().unwrap();
        for request in &requests[128..] {
            let url = reqwest::Url::parse(&request.url).unwrap();
            let pairs = url
                .query_pairs()
                .collect::<std::collections::HashMap<_, _>>();
            assert_eq!(pairs["rev"], "41");
            assert_eq!(pairs["observed_deployment_id"], "fixture-deployment");
            assert_eq!(pairs["observed_cell"], "gru");
        }
    }
    assert_eq!(
        router
            .resolve(&lifecycle_scope(129), NativeRequirement::Point)
            .await
            .unwrap_err()
            .code,
        "discovery_scope_capacity"
    );
    assert_eq!(transport.requests.lock().unwrap().len(), 131);
    assert_eq!(transport.active.load(Ordering::SeqCst), 0);
    assert_eq!(transport.released.load(Ordering::SeqCst), 131);
}

#[tokio::test]
async fn lifecycle_validated_observations_survive_initial_lease_or_capability_failure() {
    for consumed_lease in [false, true] {
        let (router, mock, clock) = fixture_router();
        if consumed_lease {
            mock.raw(200, SUCCESS, 5_100);
        } else {
            let mut unsupported = fixture();
            unsupported["endpoints"][0]["capabilities"]["read"] = json!(false);
            mock.response(200, &unsupported);
        }
        assert_eq!(
            router
                .resolve(&scope(), NativeRequirement::Point)
                .await
                .unwrap_err()
                .code,
            if consumed_lease {
                "discovery_expired"
            } else {
                "native_capability_unavailable"
            }
        );
        let now = clock.0.lock().unwrap().unix_millis / 1000;
        for number in 2..=128 {
            let target = lifecycle_scope(number);
            let mut body = fixture();
            body["scope"]["database_id"] = json!(target.database);
            body["published_at"] = json!(now);
            body["expires_at"] = json!(now + 9);
            mock.response(200, &body);
            router
                .resolve(&target, NativeRequirement::Point)
                .await
                .unwrap();
        }
        assert_eq!(
            router
                .resolve(&lifecycle_scope(129), NativeRequirement::Point)
                .await
                .unwrap_err()
                .code,
            "discovery_scope_capacity"
        );
        assert_eq!(mock.requests.lock().unwrap().len(), 128);
        if !consumed_lease {
            clock.advance(8_000);
        }
        let now = clock.0.lock().unwrap().unix_millis / 1000;
        let mut regressed = fixture();
        regressed["published_at"] = json!(now);
        regressed["expires_at"] = json!(now + 9);
        regressed["revision"] = json!("40");
        mock.response(200, &regressed);
        assert_eq!(
            router
                .resolve(&scope(), NativeRequirement::Point)
                .await
                .unwrap_err()
                .code,
            "routing_observation_regressed"
        );
        assert!(mock.requests.lock().unwrap()[128].url.ends_with("&rev=41"));
    }
}

fn current_fixture() -> Json {
    let mut body = fixture();
    let now = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap()
        .as_secs();
    body["published_at"] = json!(now);
    body["expires_at"] = json!(now + 9);
    body
}

fn ranked_fixture() -> Json {
    let mut body = current_fixture();
    let mut next = body["endpoints"][0].clone();
    next["region"] = json!("FRA");
    next["url"] = json!("https://fra.api.terrascale.tech");
    next["priority"] = json!(2);
    body["endpoints"].as_array_mut().unwrap().push(next);
    body
}

async fn point(client: &TerraScaleClient) -> Result<Point> {
    client
        .documents()
        .get(
            &scope(),
            "30000000-0000-0000-0000-000000000001".parse().unwrap(),
            "40000000-0000-0000-0000-000000000001".parse().unwrap(),
        )
        .await
}
fn absent() -> Json {
    json!({"version":1,"state":"absent","context":[],"siblings":[],"state_version":null})
}

#[tokio::test]
async fn default_and_explicit_global_base_discover_then_pin_top_region() {
    for explicit in [false, true] {
        let mock = Arc::new(Mock::default());
        let mut builder = ClientConfig::builder().api_key(native_key());
        if explicit {
            builder = builder.api_base_url(DEFAULT_API_BASE_URL);
        }
        let client = TerraScaleClient::with_transport(builder.build().unwrap(), mock.clone());
        mock.response(200, &ranked_fixture());
        mock.response(200, &absent());
        mock.response(200, &absent());
        point(&client).await.unwrap();
        point(&client).await.unwrap();
        let requests = mock.requests.lock().unwrap();
        assert_eq!(requests.len(), 3);
        assert!(requests[0].url.starts_with(ATLAS_BASE_URL));
        assert!(requests[1]
            .url
            .starts_with("https://gru.api.terrascale.tech/v1/"));
        assert_eq!(requests[1].url, requests[2].url);
    }
}

#[tokio::test]
async fn transport_5xx_and_421_fail_over_then_repin_with_default_options() {
    for failure in [None, Some(500), Some(503), Some(599), Some(421)] {
        let mock = Arc::new(Mock::default());
        let client = TerraScaleClient::with_transport(config(), mock.clone());
        mock.response(200, &ranked_fixture());
        let response = failure.map_or_else(
            || Err(Error::transport("region offline")),
            |status| {
                Ok(Response::json(
                    status,
                    &json!({"error":{"code":"unavailable","message":"region rejected request"}}),
                ))
            },
        );
        mock.replies.lock().unwrap().push_back((response, 0));
        mock.response(200, &absent());
        mock.response(200, &absent());
        point(&client).await.unwrap();
        point(&client).await.unwrap();
        let requests = mock.requests.lock().unwrap();
        assert_eq!(requests.len(), 4, "failure {failure:?}");
        assert!(requests[1]
            .url
            .starts_with("https://gru.api.terrascale.tech/v1/"));
        assert!(requests[2]
            .url
            .starts_with("https://fra.api.terrascale.tech/v1/"));
        assert_eq!(requests[2].url, requests[3].url);
    }
}

#[tokio::test]
async fn failover_excludes_failed_regions_after_the_discovery_lease_expires() {
    for failures in [1, 2] {
        let mock = Arc::new(Mock::default());
        let client = TerraScaleClient::with_transport(config(), mock.clone());
        let mut first = ranked_fixture();
        first["refreshSeconds"] = json!(1);
        let mut next = first["endpoints"][0].clone();
        next["region"] = json!("SFO");
        next["url"] = json!("https://sfo.api.terrascale.tech");
        next["priority"] = json!(3);
        first["endpoints"].as_array_mut().unwrap().push(next);
        mock.response(200, &first);
        for index in 0..failures {
            mock.replies.lock().unwrap().push_back((
                Err(Error::transport("region offline")),
                if index + 1 == failures { 1100 } else { 0 },
            ));
        }
        let mut refreshed = first;
        refreshed["refreshSeconds"] = json!(8);
        mock.response(200, &refreshed);
        mock.response(200, &absent());
        mock.response(200, &absent());
        point(&client).await.unwrap();
        point(&client).await.unwrap();
        let requests = mock.requests.lock().unwrap();
        let regions = ["gru", "fra", "sfo"];
        let expected: Vec<_> = std::iter::once("atlas")
            .chain(regions[..failures].iter().copied())
            .chain(["atlas", regions[failures], regions[failures]])
            .collect();
        let actual: Vec<_> = requests
            .iter()
            .map(|request| {
                let url = reqwest::Url::parse(&request.url).unwrap();
                url.host_str()
                    .unwrap()
                    .split('.')
                    .next()
                    .unwrap()
                    .to_owned()
            })
            .collect();
        assert_eq!(actual, expected, "failed regions: {failures}");
        assert!(requests[failures + 1].url.contains("/api/v1/discovery?"));
        assert_eq!(requests[failures + 2].url, requests[failures + 3].url);
    }
}

#[tokio::test]
async fn discovery_transport_and_5xx_failures_fall_back_to_atlas_api_proxy() {
    for failure in [None, Some(200), Some(400), Some(404), Some(503)] {
        let mock = Arc::new(Mock::default());
        let client = TerraScaleClient::with_transport(config(), mock.clone());
        let response = failure.map_or_else(|| Err(Error::transport("Atlas offline")), |status|
            Ok(Response::json(status, &json!({"error":{"code":"discovery_unconfigured","message":"offline","fix":"retry"}}))));
        mock.replies.lock().unwrap().push_back((response, 0));
        mock.response(200, &absent());
        mock.response(200, &absent());
        point(&client).await.unwrap();
        point(&client).await.unwrap();
        let requests = mock.requests.lock().unwrap();
        assert_eq!(requests.len(), 3);
        assert!(requests[0].url.starts_with(ATLAS_BASE_URL));
        assert!(requests[1]
            .url
            .starts_with("https://api.terrascale.tech/v1/"));
        assert_eq!(requests[1].url, requests[2].url);
    }
}

#[tokio::test]
async fn another_scopes_discovery_failure_does_not_interrupt_a_live_pin() {
    let mock = Arc::new(Mock::default());
    let client = TerraScaleClient::with_transport(config(), mock.clone());
    mock.response(200, &ranked_fixture());
    mock.response(200, &absent());
    point(&client).await.unwrap();
    mock.replies
        .lock()
        .unwrap()
        .push_back((Err(Error::transport("Atlas offline")), 0));
    mock.response(200, &absent());
    let mut other = scope();
    other.database = "20000000-0000-0000-0000-000000000002".parse().unwrap();
    client
        .documents()
        .get(
            &other,
            "30000000-0000-0000-0000-000000000001".parse().unwrap(),
            "40000000-0000-0000-0000-000000000001".parse().unwrap(),
        )
        .await
        .unwrap();
    mock.response(200, &absent());
    point(&client).await.unwrap();
    let requests = mock.requests.lock().unwrap();
    assert_eq!(requests.len(), 5);
    assert!(requests[3]
        .url
        .starts_with("https://api.terrascale.tech/v1/"));
    assert_eq!(requests[1].url, requests[4].url);
}

#[tokio::test]
async fn custom_base_disables_discovery_even_with_a_discovery_override() {
    let mock = Arc::new(Mock::default());
    let client = TerraScaleClient::with_transport(
        ClientConfig::builder()
            .api_key("test-key")
            .api_base_url("https://custom.test")
            .discovery_base_url("https://unused-discovery.test")
            .build()
            .unwrap(),
        mock.clone(),
    );
    mock.response(200, &absent());
    point(&client).await.unwrap();
    let requests = mock.requests.lock().unwrap();
    assert_eq!(requests.len(), 1);
    assert!(requests[0].url.starts_with("https://custom.test/v1/"));
}

#[tokio::test(start_paused = true)]
async fn discovery_timeout_falls_back_to_the_atlas_api_proxy() {
    let transport = Arc::new(LifecycleTransport::default());
    transport.pending();
    transport.reply(Ok(Response::json(200, &absent())));
    let client = TerraScaleClient::with_transport(config(), transport.clone());
    point(&client).await.unwrap();
    let requests = transport.requests.lock().unwrap();
    assert_eq!(requests.len(), 2);
    assert!(requests[0].url.starts_with(ATLAS_BASE_URL));
    assert!(requests[1]
        .url
        .starts_with("https://api.terrascale.tech/v1/"));
    assert_eq!(transport.active.load(Ordering::SeqCst), 0);
}

#[tokio::test]
async fn strict_affinity_is_direct_and_failover_affinity_honors_preference() {
    for affinity in [RegionAffinity::Strict, RegionAffinity::Failover] {
        let mock = Arc::new(Mock::default());
        let client = TerraScaleClient::with_transport(
            ClientConfig::builder()
                .api_key(native_key())
                .preferred_region("FRA")
                .region_affinity(affinity)
                .build()
                .unwrap(),
            mock.clone(),
        );
        if affinity == RegionAffinity::Failover {
            mock.response(200, &ranked_fixture());
        }
        mock.response(200, &absent());
        point(&client).await.unwrap();
        let requests = mock.requests.lock().unwrap();
        assert_eq!(
            requests.len(),
            if affinity == RegionAffinity::Strict {
                1
            } else {
                2
            }
        );
        assert!(requests
            .last()
            .unwrap()
            .url
            .starts_with("https://fra.api.terrascale.tech/v1/"));
    }
    assert!(ClientConfig::builder()
        .region_affinity(RegionAffinity::Strict)
        .build()
        .is_err());
}
fn operation() -> PreparedTransaction {
    PreparedTransaction::new(
        scope(),
        TransactionId::new(5000),
        Authority::Leaderless {},
        vec![Write {
            collection: "30000000-0000-0000-0000-000000000001".parse().unwrap(),
            document: "40000000-0000-0000-0000-000000000001".parse().unwrap(),
            schema_generation: DecimalU64(1),
            schema_digest: Digest::from_bytes([1; 32]),
            observed: vec![],
            condition: Condition::Absent {},
            value: WriteValue::Tombstone {},
        }],
    )
    .unwrap()
}
#[tokio::test]
async fn default_client_selects_authenticated_native_origin_and_status_after_downshift() {
    let mock = Arc::new(Mock::default());
    let client = TerraScaleClient::with_transport(config(), mock.clone());
    let mut body = current_fixture();
    body["endpoints"][0]["capabilities"]["read"] = json!(false);
    mock.response(200, &body);
    let operation = operation();
    assert_eq!(
        client
            .transactions()
            .commit(&operation)
            .await
            .unwrap_err()
            .code,
        "native_capability_unavailable"
    );
    mock.response(
        200,
        &serde_json::to_value(TransactionStatus {
            version: ProtocolVersion,
            state: TransactionState::Unknown,
            transaction_id: operation.transaction_id().clone(),
            effect_digest: operation.effect_digest(),
            authority: operation.authority().clone(),
            outcome: None,
        })
        .unwrap(),
    );
    let status = client.transactions().status(&operation).await.unwrap();
    assert!(!status.definitively_rejected());
    assert_eq!(mock.requests.lock().unwrap().len(), 2);
    let requests = mock.requests.lock().unwrap();
    assert!(requests[1]
        .url
        .starts_with("https://gru.api.terrascale.tech/v1/"));
    assert!(requests[1].url.ends_with("/transactions/status"));
    assert_eq!(
        requests[1]
            .headers
            .iter()
            .filter(|(name, _)| name.eq_ignore_ascii_case("authorization"))
            .count(),
        1
    );
    assert!(requests[1]
        .headers
        .contains(&("authorization".into(), format!("Bearer {}", native_key()))));
}
#[tokio::test]
async fn default_point_routes_discovery_then_native_and_transport_loss_never_replays_commit() {
    let mock = Arc::new(Mock::default());
    let client = TerraScaleClient::with_transport(
        ClientConfig::builder()
            .api_key(native_key())
            .max_retry_attempts(3)
            .retry_delay_ms(0)
            .build()
            .unwrap(),
        mock.clone(),
    );
    let mut discovery = ranked_fixture();
    discovery["refreshSeconds"] = json!(1);
    mock.response(200, &discovery);
    mock.response(
        200,
        &json!({"version":1,"state":"absent","context":[],"siblings":[],"state_version":null}),
    );
    client
        .documents()
        .get(
            &scope(),
            "30000000-0000-0000-0000-000000000001".parse().unwrap(),
            "40000000-0000-0000-0000-000000000001".parse().unwrap(),
        )
        .await
        .unwrap();
    // A supported single unconditional mutation may reach native; advanced
    // operations stay closed before dispatch under the current v1 discovery.
    let operation = PreparedTransaction::new(
        scope(),
        TransactionId::new(5000),
        Authority::Leaderless {},
        vec![Write {
            collection: "30000000-0000-0000-0000-000000000001".parse().unwrap(),
            document: "40000000-0000-0000-0000-000000000001".parse().unwrap(),
            schema_generation: DecimalU64(1),
            schema_digest: Digest::from_bytes([1; 32]),
            observed: vec![],
            condition: Condition::None {},
            value: WriteValue::Tombstone {},
        }],
    )
    .unwrap();
    mock.replies
        .lock()
        .unwrap()
        .push_back((Err(Error::transport("after body send")), 1100));
    assert!(matches!(
        client.transactions().commit(&operation).await.unwrap(),
        CommitOutcome::Uncertain(_)
    ));
    let requests = mock.requests.lock().unwrap();
    assert_eq!(requests.len(), 3);
    assert!(requests[0].url.starts_with(ATLAS_BASE_URL));
    assert!(requests[1]
        .url
        .starts_with("https://gru.api.terrascale.tech/v1/"));
    assert_eq!(requests[2].body.as_deref(), Some(operation.body()));
}

#[tokio::test]
async fn safe_native_retry_refreshes_expired_routing_before_second_dispatch() {
    let mock = Arc::new(Mock::default());
    let client = TerraScaleClient::with_transport(
        ClientConfig::builder()
            .api_key(native_key())
            .max_retry_attempts(1)
            .build()
            .unwrap(),
        mock.clone(),
    );
    let mut first = current_fixture();
    first["refreshSeconds"] = json!(1);
    mock.response(200, &first);
    let mut limited = Response::json(
        429,
        &json!({"error":{"code":"rate_limit","message":"wait"}}),
    );
    limited.headers.push(("retry-after".into(), "1".into()));
    mock.replies.lock().unwrap().push_back((Ok(limited), 0));
    let mut refreshed = current_fixture();
    refreshed["revision"] = json!("42");
    refreshed["endpoints"][0]["region"] = json!("IAD");
    refreshed["endpoints"][0]["url"] = json!("https://iad.api.terrascale.tech");
    mock.response(200, &refreshed);
    mock.response(
        200,
        &json!({"version":1,"state":"absent","context":[],"siblings":[],"state_version":null}),
    );
    client
        .documents()
        .get(
            &scope(),
            "30000000-0000-0000-0000-000000000001".parse().unwrap(),
            "40000000-0000-0000-0000-000000000001".parse().unwrap(),
        )
        .await
        .unwrap();
    let requests = mock.requests.lock().unwrap();
    assert_eq!(requests.len(), 4);
    assert!(requests[1]
        .url
        .starts_with("https://gru.api.terrascale.tech/v1/"));
    assert!(requests[2]
        .url
        .starts_with("https://atlas.terrascale.tech/api/v1/discovery?"));
    assert!(requests[2].url.ends_with("&observed_deployment_id=fixture-deployment&observed_cell=gru&observed_publisher_generation=18446744073709551615&rev=41"));
    assert!(requests[3]
        .url
        .starts_with("https://iad.api.terrascale.tech/v1/"));
}

#[derive(Debug)]
struct FailedClock;
impl DiscoveryClock for FailedClock {
    fn now(&self) -> Result<DiscoveryTime> {
        Err(Error::new(
            ErrorKind::Unavailable,
            "clock_unavailable",
            "No healthy clock source.",
        ))
    }
}
#[tokio::test]
async fn clock_failure_and_monotonic_regression_never_use_cached_admission() {
    let mock = Arc::new(Mock::default());
    let router = AtlasDiscovery::with_clock(config(), mock.clone(), Arc::new(FailedClock));
    assert_eq!(
        router
            .resolve(&scope(), NativeRequirement::Point)
            .await
            .unwrap_err()
            .code,
        "clock_unavailable"
    );
    assert!(mock.requests.lock().unwrap().is_empty());
    let (router, mock, clock) = fixture_router();
    mock.raw(200, SUCCESS, 100);
    router
        .resolve(&scope(), NativeRequirement::Point)
        .await
        .unwrap();
    clock.0.lock().unwrap().monotonic = Duration::ZERO;
    assert_eq!(
        router
            .resolve(&scope(), NativeRequirement::Point)
            .await
            .unwrap_err()
            .code,
        "discovery_clock_invalid"
    );
    assert_eq!(mock.requests.lock().unwrap().len(), 1);
}

#[tokio::test]
async fn same_epoch_registered_publisher_replacement_with_new_revision_is_allowed() {
    let (router, mock, clock) = fixture_router();
    mock.raw(200, SUCCESS, 0);
    router
        .resolve(&scope(), NativeRequirement::Point)
        .await
        .unwrap();
    clock.advance(8000);
    let mut next = fixture();
    next["published_at"] = json!(1790769608u64);
    next["expires_at"] = json!(1790769618u64);
    next["revision"] = json!("42");
    next["authority"]["publisher_id"] = json!("publisher-b");
    mock.response(200, &next);
    let resolved = router
        .resolve(&scope(), NativeRequirement::Point)
        .await
        .unwrap();
    assert_eq!(
        resolved.metadata.authority.publisher_generation,
        DecimalU64(u64::MAX)
    );
    assert_eq!(resolved.metadata.authority.publisher_id, "publisher-b");
    assert_eq!(resolved.metadata.revision, DecimalU64(42));
}

#[tokio::test]
async fn json_media_is_required_on_success_and_error_responses() {
    for status in [200, 403] {
        for media in [
            None,
            Some("text/plain"),
            Some("text/html"),
            Some("application/jsonp"),
            Some("application/json; charset=iso-8859-1"),
            Some("application/json; charset=utf-8; charset=utf-8"),
            Some("application/json; charset=\"\"utf-8\"\""),
        ] {
            let (router, mock, _) = fixture_router();
            let mut response = raw(status, if status == 200 { SUCCESS } else { FORBIDDEN });
            response
                .headers
                .retain(|(name, _)| !name.eq_ignore_ascii_case("content-type"));
            if let Some(media) = media {
                response.headers.push(("content-type".into(), media.into()));
            }
            mock.replies.lock().unwrap().push_back((Ok(response), 0));
            let error = router
                .resolve(&scope(), NativeRequirement::Point)
                .await
                .unwrap_err();
            assert_eq!(error.kind, ErrorKind::InvalidResponse);
            assert_eq!(error.status, Some(status));
            assert_eq!(mock.requests.lock().unwrap().len(), 1);
        }
    }
    for media in [
        "application/json",
        "Application/JSON; charset=UTF-8",
        "application/json;charset=\"utf-8\"",
    ] {
        let (router, mock, _) = fixture_router();
        let mut response = raw(200, SUCCESS);
        response
            .headers
            .retain(|(name, _)| !name.eq_ignore_ascii_case("content-type"));
        response.headers.push(("content-type".into(), media.into()));
        mock.replies.lock().unwrap().push_back((Ok(response), 0));
        router
            .resolve(&scope(), NativeRequirement::Point)
            .await
            .unwrap();
    }
    let (router, mock, _) = fixture_router();
    let mut response = raw(200, SUCCESS);
    response
        .headers
        .push(("Content-Type".into(), "application/json".into()));
    mock.replies.lock().unwrap().push_back((Ok(response), 0));
    assert_eq!(
        router
            .resolve(&scope(), NativeRequirement::Point)
            .await
            .unwrap_err()
            .kind,
        ErrorKind::InvalidResponse
    );
}
#[tokio::test]
async fn equal_epoch_equal_revision_publisher_substitution_does_not_advance_floor() {
    let (router, mock, clock) = fixture_router();
    mock.raw(200, SUCCESS, 0);
    router
        .resolve(&scope(), NativeRequirement::Point)
        .await
        .unwrap();
    clock.advance(8000);
    let mut next = fixture();
    next["authority"]["publisher_id"] = json!("publisher-b");
    mock.response(200, &next);
    assert_eq!(
        router
            .resolve(&scope(), NativeRequirement::Point)
            .await
            .unwrap_err()
            .code,
        "routing_observation_regressed"
    );
    next["published_at"] = json!(1790769608u64);
    next["expires_at"] = json!(1790769618u64);
    next["revision"] = json!("42");
    mock.response(200, &next);
    let resolved = router
        .resolve(&scope(), NativeRequirement::Point)
        .await
        .unwrap();
    assert_eq!(resolved.metadata.authority.publisher_id, "publisher-b");
    assert_eq!(resolved.metadata.revision, DecimalU64(42));
    let requests = mock.requests.lock().unwrap();
    assert_eq!(requests.len(), 3);
    assert!(requests[2]
        .url
        .ends_with("&observed_publisher_generation=18446744073709551615&rev=41"));
}

#[tokio::test]
async fn receiver_unix_bounds_are_inclusive_and_publication_span_is_at_most_ten() {
    for (published, expires) in [(1, 10), (253_402_300_790, 253_402_300_799)] {
        let (router, mock, clock) = fixture_router();
        clock.0.lock().unwrap().unix_millis = published * 1000;
        let mut body = fixture();
        body["published_at"] = json!(published);
        body["expires_at"] = json!(expires);
        mock.response(200, &body);
        let resolved = router
            .resolve(&scope(), NativeRequirement::Point)
            .await
            .unwrap();
        assert_eq!(resolved.metadata.published_at, published);
        assert_eq!(resolved.metadata.expires_at, expires);
    }
    let (router, mock, _) = fixture_router();
    let mut body = fixture();
    body["expires_at"] = json!(1790769610u64);
    body["refreshSeconds"] = json!(10);
    mock.response(200, &body);
    assert_eq!(
        router
            .resolve(&scope(), NativeRequirement::Point)
            .await
            .unwrap()
            .metadata
            .refresh_seconds,
        10
    );
    let (router, mock, _) = fixture_router();
    body["expires_at"] = json!(1790769611u64);
    mock.response(200, &body);
    assert_eq!(
        router
            .resolve(&scope(), NativeRequirement::Point)
            .await
            .unwrap_err()
            .kind,
        ErrorKind::InvalidResponse
    );
}
