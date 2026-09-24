//! Liveness, dependency readiness, and bounded private Prometheus metrics.

use std::{
    array,
    sync::atomic::{AtomicBool, AtomicU64, Ordering},
    time::{Duration, Instant},
};

use axum::{
    Json, Router,
    extract::State,
    http::{HeaderMap, StatusCode, header},
    response::IntoResponse,
    routing::get,
};
use serde::Serialize;
use subtle::ConstantTimeEq;
use utoipa::OpenApi;

use crate::app::AppState;

const METHODS: [&str; 8] = [
    "DELETE", "GET", "HEAD", "OPTIONS", "PATCH", "POST", "PUT", "OTHER",
];
const EVENT_KINDS: [&str; 5] = ["authentication", "session", "webhook", "storage", "other"];
const OUTCOMES: [&str; 2] = ["success", "failure"];

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
    events: [AtomicU64; EVENT_KINDS.len() * OUTCOMES.len()],
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
            events: array::from_fn(|_| AtomicU64::new(0)),
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

    pub fn record_event(&self, kind: &str, success: bool) {
        let kind_index = EVENT_KINDS
            .iter()
            .position(|item| *item == kind)
            .unwrap_or(EVENT_KINDS.len() - 1);
        let outcome_index = usize::from(!success);
        self.events[kind_index * OUTCOMES.len() + outcome_index].fetch_add(1, Ordering::Relaxed);
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
            "# HELP ts_auth_ready Whether the HTTP process is ready to serve requests.",
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
            "# HELP ts_auth_dependency_ready Whether a required dependency is ready.",
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
            "# HELP ts_auth_events_total Closed lifecycle event outcomes.",
        );
        metric(&mut output, "# TYPE ts_auth_events_total counter");
        for (kind_index, kind) in EVENT_KINDS.iter().enumerate() {
            for (outcome_index, outcome) in OUTCOMES.iter().enumerate() {
                let count = self.events[kind_index * OUTCOMES.len() + outcome_index]
                    .load(Ordering::Relaxed);
                if count != 0 {
                    metric(
                        &mut output,
                        &format!(
                            "ts_auth_events_total{{kind=\"{kind}\",outcome=\"{outcome}\"}} {count}"
                        ),
                    );
                }
            }
        }
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

#[utoipa::path(get, path = "/readyz", responses(
    (status = 200, body = ReadyBody),
    (status = 503, body = ReadyBody)
))]
async fn readyz(State(state): State<AppState>) -> impl IntoResponse {
    match state.store.health().await {
        Ok(health) if health.healthy => {
            state.metrics.set_ready(true);
            state.metrics.set_dependency_ready("terrabase", true);
            (
                StatusCode::OK,
                Json(ReadyBody {
                    status: "ready",
                    region: Some(health.region),
                }),
            )
                .into_response()
        }
        Ok(health) => {
            state.metrics.set_ready(false);
            state.metrics.set_dependency_ready("terrabase", false);
            (
                StatusCode::SERVICE_UNAVAILABLE,
                Json(ReadyBody {
                    status: "not_ready",
                    region: Some(health.region),
                }),
            )
                .into_response()
        }
        Err(error) => {
            tracing::warn!(%error, "readiness check failed");
            state.metrics.set_ready(false);
            state.metrics.set_dependency_ready("terrabase", false);
            (
                StatusCode::SERVICE_UNAVAILABLE,
                Json(ReadyBody {
                    status: "not_ready",
                    region: None,
                }),
            )
                .into_response()
        }
    }
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
    if expected.is_empty()
        || presented.is_empty()
        || !bool::from(expected.as_bytes().ct_eq(presented.as_bytes()))
    {
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
        metrics.record_event("webhook", true);
        metrics.record_event("tenant-123", false);
        metrics.record_request("GET", 200, Duration::from_millis(25));
        metrics.record_request("/secret/path", 429, Duration::from_millis(10));

        let text = metrics.render_prometheus();
        assert!(text.contains("ts_auth_ready 1\n"));
        assert!(text.contains("ts_auth_liveness 0\n"));
        assert!(text.contains("dependency=\"terrabase\"} 1"));
        assert!(text.contains("kind=\"webhook\",outcome=\"success\"} 1"));
        assert!(text.contains("kind=\"other\",outcome=\"failure\"} 1"));
        assert!(text.contains("method=\"GET\",status_class=\"2xx\"} 1"));
        assert!(text.contains("method=\"OTHER\",status_class=\"4xx\"} 1"));
        assert!(!text.contains("customer-secret"));
        assert!(!text.contains("/secret/path"));
    }
}
