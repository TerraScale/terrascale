//! Private Prometheus and lifecycle probe adapter.

use std::sync::Arc;

use axum::Router;
use axum::extract::State;
use axum::http::StatusCode;
use axum::http::header::CONTENT_TYPE;
use axum::response::IntoResponse;
use axum::routing::get;
use terrabase::observability::MAX_PROMETHEUS_BODY_BYTES;
use terrabase::observability::MetricsRegistry;
use terrabase::observability::PROMETHEUS_CONTENT_TYPE;

/// Builds the private operator router. It has no tenant-derived routes,
/// labels, query values, or diagnostics.
#[must_use]
pub fn router(metrics: Arc<MetricsRegistry>) -> Router {
    Router::new()
        .route("/metrics", get(prometheus))
        .route("/live", get(liveness))
        .route("/ready", get(readiness))
        .route("/startup", get(startup))
        .with_state(metrics)
}

async fn prometheus(State(metrics): State<Arc<MetricsRegistry>>) -> impl IntoResponse {
    let body = metrics.snapshot().render_prometheus();
    if body.len() > MAX_PROMETHEUS_BODY_BYTES {
        return (
            StatusCode::INTERNAL_SERVER_ERROR,
            [(CONTENT_TYPE, "text/plain; charset=utf-8")],
            "metrics body exceeded its contract\n".to_owned(),
        );
    }
    (StatusCode::OK, [(CONTENT_TYPE, PROMETHEUS_CONTENT_TYPE)], body)
}

async fn liveness(State(metrics): State<Arc<MetricsRegistry>>) -> impl IntoResponse {
    probe(metrics.snapshot().is_live(), "live")
}

async fn readiness(State(metrics): State<Arc<MetricsRegistry>>) -> impl IntoResponse {
    probe(metrics.snapshot().is_ready(), "ready")
}

async fn startup(State(metrics): State<Arc<MetricsRegistry>>) -> impl IntoResponse {
    probe(metrics.snapshot().is_started(), "started")
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
