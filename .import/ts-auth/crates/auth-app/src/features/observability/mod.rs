//! Liveness, dependency readiness, and bounded private Prometheus metrics.

use std::{
    array,
    sync::{
        Arc,
        atomic::{AtomicBool, AtomicU64, Ordering},
    },
    time::{Duration, Instant},
};

use auth_security::tokens::{fixed_time_equals, sha256_hex};
use axum::{
    Json, Router,
    extract::State,
    http::{HeaderMap, StatusCode, header},
    response::{IntoResponse, Response},
    routing::get,
};
use serde::Serialize;
use tokio::sync::{Mutex, watch};
use utoipa::OpenApi;

use crate::app::AppState;

const METHODS: [&str; 8] = [
    "DELETE", "GET", "HEAD", "OPTIONS", "PATCH", "POST", "PUT", "OTHER",
];
const READINESS_PROBE_TIMEOUT: Duration = Duration::from_secs(2);
const READINESS_CACHE_TTL: Duration = Duration::from_secs(1);

#[derive(Clone)]
struct ReadinessObservation {
    completed_at: Instant,
    ready: bool,
    region: Option<String>,
}

struct InFlightProbe {
    id: Arc<()>,
    receiver: watch::Receiver<Option<ReadinessObservation>>,
}

#[derive(Default)]
struct ReadinessCache {
    latest: Option<ReadinessObservation>,
    in_flight: Option<InFlightProbe>,
}

/// Process-local counters with a deliberately closed label set.
///
/// No route, tenant, instance, database, or credential value is admitted as a
/// metric label. Every counter uses atomics so handlers do not contend on a
/// shared lock and can be scraped while requests are being recorded.
pub struct AuthMetrics {
    started_at: Instant,
    ready: AtomicBool,
    live: AtomicBool,
    terrabase_ready: AtomicBool,
    readiness_cache: Mutex<ReadinessCache>,
    requests: [AtomicU64; METHODS.len() * 5],
    request_duration_nanos: [AtomicU64; METHODS.len() * 5],
}

impl AuthMetrics {
    pub fn new() -> Self {
        Self {
            started_at: Instant::now(),
            ready: AtomicBool::new(false),
            live: AtomicBool::new(true),
            terrabase_ready: AtomicBool::new(false),
            readiness_cache: Mutex::new(ReadinessCache::default()),
            requests: array::from_fn(|_| AtomicU64::new(0)),
            request_duration_nanos: array::from_fn(|_| AtomicU64::new(0)),
        }
    }

    pub fn is_ready(&self) -> bool {
        self.ready.load(Ordering::Relaxed)
    }

    pub fn is_live(&self) -> bool {
        self.live.load(Ordering::Relaxed)
    }

    pub fn set_ready(&self, value: bool) {
        self.ready.store(value, Ordering::Relaxed);
    }

    pub fn set_liveness(&self, value: bool) {
        self.live.store(value, Ordering::Relaxed);
    }

    /// Only the fixed TerraBase dependency label is supported.
    pub fn set_dependency_ready(&self, dependency: &str, value: bool) -> bool {
        if dependency != "terrabase" {
            return false;
        }
        self.terrabase_ready.store(value, Ordering::Relaxed);
        true
    }

    pub fn record_request(&self, method: &str, status_code: u16, elapsed: Duration) {
        let method_index = METHODS
            .iter()
            .position(|item| *item == method)
            .unwrap_or(METHODS.len() - 1);
        let status_class = usize::from((status_code / 100).clamp(1, 5) - 1);
        let index = method_index * 5 + status_class;
        self.requests[index].fetch_add(1, Ordering::Relaxed);
        self.request_duration_nanos[index].fetch_add(
            elapsed.as_nanos().min(u128::from(u64::MAX)) as u64,
            Ordering::Relaxed,
        );
    }

    pub fn render_prometheus(&self) -> String {
        let mut output = String::with_capacity(2048);
        metric(&mut output, "# HELP ts_auth_build_info Build information.");
        metric(&mut output, "# TYPE ts_auth_build_info gauge");
        metric(&mut output, "ts_auth_build_info{service=\"ts-auth\"} 1");
        metric(
            &mut output,
            "# HELP ts_auth_process_uptime_seconds Process uptime in seconds.",
        );
        metric(&mut output, "# TYPE ts_auth_process_uptime_seconds gauge");
        metric(
            &mut output,
            &format!(
                "ts_auth_process_uptime_seconds {:.3}",
                self.started_at.elapsed().as_secs_f64()
            ),
        );
        metric(
            &mut output,
            "# HELP ts_auth_ready Whether the latest completed /readyz observation found the process ready (0 until the first observation).",
        );
        metric(&mut output, "# TYPE ts_auth_ready gauge");
        metric(
            &mut output,
            &format!("ts_auth_ready {}", u8::from(self.is_ready())),
        );
        metric(
            &mut output,
            "# HELP ts_auth_liveness Whether the process liveness probe is available.",
        );
        metric(&mut output, "# TYPE ts_auth_liveness gauge");
        metric(
            &mut output,
            &format!("ts_auth_liveness {}", u8::from(self.is_live())),
        );
        metric(
            &mut output,
            "# HELP ts_auth_dependency_ready Whether the latest completed /readyz observation found a required dependency ready.",
        );
        metric(&mut output, "# TYPE ts_auth_dependency_ready gauge");
        metric(
            &mut output,
            &format!(
                "ts_auth_dependency_ready{{dependency=\"terrabase\"}} {}",
                u8::from(self.terrabase_ready.load(Ordering::Relaxed))
            ),
        );
        metric(
            &mut output,
            "# HELP ts_auth_http_requests_total HTTP requests by bounded method and status class.",
        );
        metric(&mut output, "# TYPE ts_auth_http_requests_total counter");
        metric(
            &mut output,
            "# HELP ts_auth_http_request_duration_seconds_total Cumulative HTTP request duration.",
        );
        metric(
            &mut output,
            "# TYPE ts_auth_http_request_duration_seconds_total counter",
        );
        for (method_index, method) in METHODS.iter().enumerate() {
            for status_index in 0..5 {
                let index = method_index * 5 + status_index;
                let count = self.requests[index].load(Ordering::Relaxed);
                if count == 0 {
                    continue;
                }
                let nanos = self.request_duration_nanos[index].load(Ordering::Relaxed);
                let status_class = status_index + 1;
                metric(
                    &mut output,
                    &format!(
                        "ts_auth_http_requests_total{{method=\"{method}\",status_class=\"{status_class}xx\"}} {count}"
                    ),
                );
                metric(
                    &mut output,
                    &format!(
                        "ts_auth_http_request_duration_seconds_total{{method=\"{method}\",status_class=\"{status_class}xx\"}} {:.6}",
                        nanos as f64 / 1_000_000_000.0
                    ),
                );
            }
        }
        output
    }
}

impl Default for AuthMetrics {
    fn default() -> Self {
        Self::new()
    }
}

fn metric(output: &mut String, line: &str) {
    output.push_str(line);
    output.push('\n');
}

#[derive(Serialize, utoipa::ToSchema)]
struct StatusBody {
    status: &'static str,
}

#[derive(Serialize, utoipa::ToSchema)]
struct ReadyBody {
    status: &'static str,
    #[serde(skip_serializing_if = "Option::is_none")]
    region: Option<String>,
}

impl ReadinessObservation {
    fn into_response(self) -> Response {
        let (status_code, status) = if self.ready {
            (StatusCode::OK, "ready")
        } else {
            (StatusCode::SERVICE_UNAVAILABLE, "not_ready")
        };
        (
            status_code,
            Json(ReadyBody {
                status,
                region: self.region,
            }),
        )
            .into_response()
    }
}

pub fn router() -> Router<AppState> {
    Router::new()
        .route("/healthz", get(healthz))
        .route("/readyz", get(readyz))
        .route("/metrics", get(metrics))
}

#[utoipa::path(get, path = "/healthz", responses((status = 200, body = StatusBody)))]
async fn healthz(State(state): State<AppState>) -> Json<StatusBody> {
    state.metrics.set_liveness(true);
    Json(StatusBody { status: "ok" })
}

async fn readiness_observation(state: &AppState) -> ReadinessObservation {
    let (probe_id, mut receiver, start_probe) = {
        let mut cache = state.metrics.readiness_cache.lock().await;
        if let Some(latest) = cache.latest.as_ref()
            && Instant::now().saturating_duration_since(latest.completed_at) < READINESS_CACHE_TTL
        {
            return latest.clone();
        }

        if let Some(probe_id) = cache.in_flight.as_ref().and_then(|probe| {
            probe
                .receiver
                .has_changed()
                .is_err()
                .then(|| Arc::clone(&probe.id))
        }) {
            let observation = failed_readiness_observation();
            let _ =
                cache_failed_probe_if_current(&state.metrics, &mut cache, &probe_id, &observation);
            return observation;
        }

        if let Some(probe) = &cache.in_flight {
            (Arc::clone(&probe.id), probe.receiver.clone(), None)
        } else {
            let (sender, receiver) = watch::channel(None);
            let probe_id = Arc::new(());
            cache.in_flight = Some(InFlightProbe {
                id: Arc::clone(&probe_id),
                receiver: receiver.clone(),
            });
            (probe_id, receiver, Some(sender))
        }
    };

    if let Some(sender) = start_probe {
        let store = Arc::clone(&state.store);
        let metrics = Arc::clone(&state.metrics);
        let worker_probe_id = Arc::clone(&probe_id);
        tokio::spawn(async move {
            let result = tokio::time::timeout(READINESS_PROBE_TIMEOUT, store.health()).await;
            let observation = match result {
                Ok(Ok(health)) => ReadinessObservation {
                    completed_at: Instant::now(),
                    ready: health.healthy,
                    region: Some(health.region),
                },
                Ok(Err(error)) => {
                    tracing::warn!(%error, "readiness check failed");
                    ReadinessObservation {
                        completed_at: Instant::now(),
                        ready: false,
                        region: None,
                    }
                }
                Err(_) => {
                    tracing::warn!("readiness check timed out");
                    ReadinessObservation {
                        completed_at: Instant::now(),
                        ready: false,
                        region: None,
                    }
                }
            };

            let mut cache = metrics.readiness_cache.lock().await;
            let _ =
                publish_readiness_observation(&metrics, &mut cache, &worker_probe_id, &observation);
            drop(cache);
            let _ = sender.send(Some(observation));
        });
    }

    let observation = receiver.wait_for(Option::is_some).await.map(|observation| {
        observation
            .as_ref()
            .expect("readiness observation is present")
            .clone()
    });
    match observation {
        Ok(observation) => observation,
        Err(_) => {
            let observation = failed_readiness_observation();
            let mut cache = state.metrics.readiness_cache.lock().await;
            let _ =
                cache_failed_probe_if_current(&state.metrics, &mut cache, &probe_id, &observation);
            observation
        }
    }
}

fn failed_readiness_observation() -> ReadinessObservation {
    ReadinessObservation {
        completed_at: Instant::now(),
        ready: false,
        region: None,
    }
}

fn publish_readiness_observation(
    metrics: &AuthMetrics,
    cache: &mut ReadinessCache,
    probe_id: &Arc<()>,
    observation: &ReadinessObservation,
) -> bool {
    if !cache
        .in_flight
        .as_ref()
        .is_some_and(|probe| Arc::ptr_eq(&probe.id, probe_id))
    {
        return false;
    }

    metrics.set_ready(observation.ready);
    metrics.set_dependency_ready("terrabase", observation.ready);
    cache.latest = Some(observation.clone());
    cache.in_flight = None;
    true
}

fn cache_failed_probe_if_current(
    metrics: &AuthMetrics,
    cache: &mut ReadinessCache,
    probe_id: &Arc<()>,
    observation: &ReadinessObservation,
) -> bool {
    if !cache.in_flight.as_ref().is_some_and(|probe| {
        Arc::ptr_eq(&probe.id, probe_id) && probe.receiver.has_changed().is_err()
    }) {
        return false;
    }
    publish_readiness_observation(metrics, cache, probe_id, observation)
}

#[utoipa::path(get, path = "/readyz", responses(
    (status = 200, body = ReadyBody),
    (status = 503, body = ReadyBody)
))]
async fn readyz(State(state): State<AppState>) -> Response {
    readiness_observation(&state).await.into_response()
}

#[utoipa::path(get, path = "/metrics", params(
    ("X-TerraScale-Metrics-Key" = String, Header, description = "Private operator scrape key")
), responses(
    (status = 200, description = "Prometheus text exposition", content_type = "text/plain"),
    (status = 401, description = "Missing or invalid scrape key")
))]
async fn metrics(State(state): State<AppState>, headers: HeaderMap) -> impl IntoResponse {
    let expected = state.options.metrics_key.as_deref().unwrap_or_default();
    let presented = headers
        .get("X-TerraScale-Metrics-Key")
        .and_then(|value| value.to_str().ok())
        .unwrap_or_default();
    if expected.is_empty() || presented.is_empty() {
        return StatusCode::UNAUTHORIZED.into_response();
    }
    let expected_digest = sha256_hex(expected);
    let presented_digest = sha256_hex(presented);
    if !fixed_time_equals(&expected_digest, &presented_digest) {
        return StatusCode::UNAUTHORIZED.into_response();
    }

    (
        [(
            header::CONTENT_TYPE,
            "text/plain; version=0.0.4; charset=utf-8",
        )],
        state.metrics.render_prometheus(),
    )
        .into_response()
}

#[derive(OpenApi)]
#[openapi(
    paths(healthz, readyz, metrics),
    components(schemas(StatusBody, ReadyBody)),
    tags((name = "observability", description = "Health and private operator metrics"))
)]
struct ObservabilityApi;

pub fn openapi() -> utoipa::openapi::OpenApi {
    ObservabilityApi::openapi()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn metrics_keep_only_bounded_labels_and_track_totals() {
        let metrics = AuthMetrics::new();
        metrics.set_ready(true);
        metrics.set_liveness(false);
        assert!(metrics.set_dependency_ready("terrabase", true));
        assert!(!metrics.set_dependency_ready("customer-secret", false));
        metrics.record_request("GET", 200, Duration::from_millis(25));
        metrics.record_request("/secret/path", 429, Duration::from_millis(10));

        let text = metrics.render_prometheus();
        assert!(text.contains("ts_auth_ready 1\n"));
        assert!(text.contains("ts_auth_liveness 0\n"));
        assert!(text.contains("dependency=\"terrabase\"} 1"));
        assert!(!text.contains("ts_auth_events_total"));
        assert!(text.contains("method=\"GET\",status_class=\"2xx\"} 1"));
        assert!(text.contains("method=\"OTHER\",status_class=\"4xx\"} 1"));
        assert!(!text.contains("customer-secret"));
        assert!(!text.contains("/secret/path"));
    }

    #[tokio::test]
    async fn closed_probe_failure_updates_the_latest_observation_and_gauges() {
        let metrics = AuthMetrics::new();
        metrics.set_ready(true);
        assert!(metrics.set_dependency_ready("terrabase", true));

        let probe_id = Arc::new(());
        let (sender, receiver) = watch::channel(None);
        {
            let mut cache = metrics.readiness_cache.lock().await;
            cache.latest = Some(ReadinessObservation {
                completed_at: Instant::now() - READINESS_CACHE_TTL - Duration::from_millis(1),
                ready: true,
                region: Some("GRU".to_owned()),
            });
            cache.in_flight = Some(InFlightProbe {
                id: Arc::clone(&probe_id),
                receiver,
            });
        }
        drop(sender);

        let observation = failed_readiness_observation();
        {
            let mut cache = metrics.readiness_cache.lock().await;
            assert!(cache_failed_probe_if_current(
                &metrics,
                &mut cache,
                &probe_id,
                &observation,
            ));
            assert!(cache.in_flight.is_none());
            let latest = cache
                .latest
                .as_ref()
                .expect("failed observation should be cached");
            assert!(!latest.ready);
            assert!(latest.region.is_none());
            assert!(latest.completed_at.elapsed() < READINESS_CACHE_TTL);
        }

        assert!(!metrics.is_ready());
        assert!(!metrics.terrabase_ready.load(Ordering::Relaxed));
    }

    #[tokio::test]
    async fn stale_probe_failure_does_not_replace_a_newer_flight_or_observation() {
        let metrics = AuthMetrics::new();
        metrics.set_ready(true);
        assert!(metrics.set_dependency_ready("terrabase", true));

        let stale_probe_id = Arc::new(());
        let (stale_sender, stale_receiver) = watch::channel::<Option<ReadinessObservation>>(None);
        drop(stale_sender);

        let current_probe_id = Arc::new(());
        let (current_sender, current_receiver) = watch::channel(None);
        {
            let mut cache = metrics.readiness_cache.lock().await;
            cache.latest = Some(ReadinessObservation {
                completed_at: Instant::now() - READINESS_CACHE_TTL - Duration::from_millis(1),
                ready: true,
                region: Some("GRU".to_owned()),
            });
            cache.in_flight = Some(InFlightProbe {
                id: Arc::clone(&current_probe_id),
                receiver: current_receiver,
            });
        }
        drop(stale_receiver);

        let observation = failed_readiness_observation();
        {
            let mut cache = metrics.readiness_cache.lock().await;
            assert!(!cache_failed_probe_if_current(
                &metrics,
                &mut cache,
                &stale_probe_id,
                &observation,
            ));
            let current = cache
                .in_flight
                .as_ref()
                .expect("newer in-flight probe should remain");
            assert!(Arc::ptr_eq(&current.id, &current_probe_id));
            assert!(current.receiver.has_changed().is_ok());
            assert!(
                cache
                    .latest
                    .as_ref()
                    .expect("previous completed observation should remain")
                    .ready
            );
        }

        assert!(metrics.is_ready());
        assert!(metrics.terrabase_ready.load(Ordering::Relaxed));
        drop(current_sender);
    }
}
