//! Private Prometheus and lifecycle probe adapter.

use std::sync::Arc;
use std::time::Duration;

use axum::Router;
use axum::extract::DefaultBodyLimit;
use axum::extract::Request;
use axum::extract::State;
use axum::http::StatusCode;
use axum::http::header::CONTENT_TYPE;
use axum::middleware::Next;
use axum::middleware::from_fn_with_state;
use axum::response::IntoResponse;
use axum::response::Response;
use axum::routing::get;
use terrabase::observability::LIVENESS_PATH;
use terrabase::observability::MetricsRegistry;
use terrabase::observability::PROMETHEUS_CONTENT_TYPE;
use terrabase::observability::PROMETHEUS_PATH;
use terrabase::observability::READINESS_PATH;
use terrabase::observability::STARTUP_PATH;

const MAX_OPERATOR_REQUESTS: usize = 16;
const MAX_HEADER_COUNT: usize = 32;
const MAX_HEADER_BYTES: usize = 8 * 1024;
const MAX_URI_BYTES: usize = 2 * 1024;
const OPERATOR_TIMEOUT: Duration = Duration::from_secs(5);

#[derive(Clone)]
struct OperatorState {
    metrics: Arc<MetricsRegistry>,
    permits: Arc<tokio::sync::Semaphore>,
}

/// Builds the private operator router. It has no tenant-derived routes,
/// labels, query values, or diagnostics.
#[must_use]
pub fn router(metrics: Arc<MetricsRegistry>) -> Router {
    let state = OperatorState {
        metrics,
        permits: Arc::new(tokio::sync::Semaphore::new(MAX_OPERATOR_REQUESTS)),
    };
    Router::new()
        .route(PROMETHEUS_PATH, get(prometheus))
        .route(LIVENESS_PATH, get(liveness))
        .route(READINESS_PATH, get(readiness))
        .route(STARTUP_PATH, get(startup))
        .layer(DefaultBodyLimit::max(0))
        .layer(from_fn_with_state(state.clone(), bounded_request))
        .with_state(state)
}

async fn bounded_request(
    State(state): State<OperatorState>,
    request: Request,
    next: Next,
) -> Response {
    let header_bytes = request
        .headers()
        .iter()
        .map(|(name, value)| name.as_str().len().saturating_add(value.as_bytes().len()))
        .sum::<usize>();
    if request.headers().len() > MAX_HEADER_COUNT
        || header_bytes > MAX_HEADER_BYTES
        || request.uri().to_string().len() > MAX_URI_BYTES
    {
        return (
            StatusCode::REQUEST_HEADER_FIELDS_TOO_LARGE,
            "operator request exceeds its bound\n",
        )
            .into_response();
    }
    let Ok(Ok(permit)) = tokio::time::timeout(OPERATOR_TIMEOUT, state.permits.acquire()).await
    else {
        return (StatusCode::SERVICE_UNAVAILABLE, "operator request capacity exhausted\n")
            .into_response();
    };
    let response = tokio::time::timeout(OPERATOR_TIMEOUT, next.run(request)).await;
    drop(permit);
    response.unwrap_or_else(|_| {
        (StatusCode::GATEWAY_TIMEOUT, "operator request exceeded its deadline\n").into_response()
    })
}

async fn prometheus(State(state): State<OperatorState>) -> impl IntoResponse {
    match state.metrics.snapshot().try_render_prometheus() {
        Ok(body) => (StatusCode::OK, [(CONTENT_TYPE, PROMETHEUS_CONTENT_TYPE)], body),
        Err(_) => (
            StatusCode::INTERNAL_SERVER_ERROR,
            [(CONTENT_TYPE, "text/plain; charset=utf-8")],
            "metrics body exceeded its contract\n".to_owned(),
        ),
    }
}

async fn liveness(State(state): State<OperatorState>) -> impl IntoResponse {
    probe(state.metrics.snapshot().is_live(), "live")
}

async fn readiness(State(state): State<OperatorState>) -> impl IntoResponse {
    probe(state.metrics.snapshot().is_ready(), "ready")
}

async fn startup(State(state): State<OperatorState>) -> impl IntoResponse {
    probe(state.metrics.snapshot().is_started(), "started")
}

fn probe(accepted: bool, name: &'static str) -> impl IntoResponse {
    let status = if accepted { StatusCode::OK } else { StatusCode::SERVICE_UNAVAILABLE };
    (status, [(CONTENT_TYPE, "text/plain; charset=utf-8")], format!("{name}\n"))
}

#[cfg(test)]
mod tests {
    use super::*;
    use terrabase::observability::Component;
    use terrabase::observability::HealthStatus;
    use terrabase::observability::ServiceState;

    #[test]
    fn lifecycle_snapshots_keep_probe_semantics_distinct() {
        let metrics = MetricsRegistry::new();
        let starting = metrics.snapshot();
        assert!(starting.is_live());
        assert!(!starting.is_started());
        assert!(!starting.is_ready());

        for component in [Component::Storage, Component::Replication, Component::Membership] {
            metrics.set_component_health(component, HealthStatus::Healthy);
        }
        metrics.transition_service(ServiceState::Serving).expect("serving transition");
        let serving = metrics.snapshot();
        assert!(serving.is_live());
        assert!(serving.is_started());
        assert!(serving.is_ready());

        metrics.transition_service(ServiceState::Draining).expect("draining transition");
        let draining = metrics.snapshot();
        assert!(draining.is_live());
        assert!(draining.is_started());
        assert!(!draining.is_ready());

        metrics.transition_service(ServiceState::Stopped).expect("stopped transition");
        let stopped = metrics.snapshot();
        assert!(!stopped.is_live());
        assert!(stopped.is_started());
        assert!(!stopped.is_ready());
    }
}
