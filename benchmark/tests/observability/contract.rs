//! Cross-module observability contract checks.
//!
//! Keeping these checks outside the implementation tests makes the adapter
//! contract visible and prevents a refactor from silently changing it.

use std::time::Duration;

use terrabase::observability::{
    Component, HealthStatus, MetricsRegistry, Operation, Outcome, Region, ServiceState,
    StorageTier, redact_diagnostic,
};

#[test]
fn exposition_has_one_type_declaration_per_family() {
    let metrics = MetricsRegistry::new();
    let text = metrics.render_prometheus();
    for family in [
        "terrabase_liveness",
        "terrabase_started",
        "terrabase_ready",
        "terrabase_requests_total",
        "terrabase_request_duration_seconds",
        "terrabase_replication_lag_seconds",
        "terrabase_storage_bytes",
        "terrabase_storage_capacity_bytes",
        "terrabase_active_members",
        "terrabase_component_health",
    ] {
        assert_eq!(
            text.matches(&format!("# TYPE {family} ")).count(),
            1,
            "family {family} must have exactly one type declaration"
        );
    }
}

#[test]
fn labels_are_bounded_even_when_observations_are_invalid_or_unconfigured() {
    let metrics = MetricsRegistry::new();
    metrics.record_request(Region::Unknown, Operation::Other, Outcome::Rejected, Duration::ZERO);
    metrics.set_replication_lag(Region::Unknown, Duration::from_secs(1));
    metrics.set_active_members(Region::Unknown, 0);
    let text = metrics.render_prometheus();
    assert!(!text.contains("tenant_id"));
    assert!(!text.contains("request_id"));
    assert!(!text.contains("ocid"));
    assert!(text.contains("region=\"unknown\""));
}

#[test]
fn required_failure_is_not_hidden_by_optional_component_health() {
    let health = terrabase::observability::HealthRegistry::new();
    health.set(Component::Storage, HealthStatus::Unhealthy);
    health.set(Component::Backup, HealthStatus::Healthy);
    let report = health.snapshot();
    assert_eq!(report.status(), HealthStatus::Unhealthy);
    assert!(!report.is_ready());
    assert_eq!(report.readiness_status_code(), 503);
}

#[test]
fn startup_liveness_and_readiness_are_independent_monotonic_facts() {
    let metrics = MetricsRegistry::new();
    for component in
        [Component::Storage, Component::Replication, Component::Membership, Component::ControlPlane]
    {
        metrics.set_component_health(component, HealthStatus::Healthy);
    }
    let starting = metrics.snapshot();
    assert!(starting.is_live());
    assert!(!starting.is_started());
    assert!(!starting.is_ready());

    metrics.transition_service(ServiceState::Serving).expect("the service may complete startup");
    let serving = metrics.snapshot();
    assert!(serving.is_live());
    assert!(serving.is_started());
    assert!(serving.is_ready());

    metrics.transition_service(ServiceState::Draining).expect("the service may begin its drain");
    let draining = metrics.snapshot();
    assert!(draining.is_live());
    assert!(draining.is_started());
    assert!(!draining.is_ready());
}

#[test]
fn immutable_snapshot_is_the_prometheus_source_of_truth() {
    let metrics = MetricsRegistry::new();
    metrics.record_request(
        Region::Sfo,
        Operation::Write,
        Outcome::Success,
        Duration::from_millis(12),
    );
    metrics.set_replication_lag(Region::Fra, Duration::from_millis(250));
    metrics.set_storage_bytes(StorageTier::Tier2, 4_096);
    metrics.set_storage_capacity_bytes(StorageTier::Tier2, 16_384);
    metrics.set_active_members(Region::Gru, 2);

    let snapshot = metrics.snapshot();
    assert_eq!(snapshot.requests().len(), 1);
    assert_eq!(snapshot.requests()[0].total(), 1);
    assert_eq!(snapshot.requests()[0].duration().count(), 1);
    assert_eq!(snapshot.replication_lag()[0].seconds(), 0.25);
    assert_eq!(snapshot.storage()[0].bytes(), 4_096);
    assert_eq!(snapshot.storage_capacity()[0].bytes(), 16_384);
    assert_eq!(snapshot.active_members()[0].members(), 2);
    assert_eq!(snapshot.component_health().len(), 6);
    assert_eq!(snapshot.render_prometheus(), metrics.render_prometheus());
}

#[test]
fn every_exposed_label_name_belongs_to_its_closed_family() {
    let metrics = MetricsRegistry::new();
    metrics.record_request(
        Region::Gru,
        Operation::Read,
        Outcome::Success,
        Duration::from_millis(1),
    );
    metrics.set_replication_lag(Region::Gru, Duration::ZERO);
    metrics.set_storage_bytes(StorageTier::Tier1, 0);
    metrics.set_active_members(Region::Gru, 2);

    for line in metrics.render_prometheus().lines().filter(|line| !line.starts_with('#')) {
        let Some((name, rest)) = line.split_once('{') else { continue };
        let labels = rest.split_once('}').expect("labeled series closes its label set").0;
        let keys: Vec<_> = labels
            .split(',')
            .map(|label| label.split_once('=').expect("label has a value").0)
            .collect();
        let allowed: &[&str] = if name == "terrabase_requests_total"
            || name == "terrabase_request_duration_seconds_sum"
            || name == "terrabase_request_duration_seconds_count"
        {
            &["region", "operation", "outcome"]
        } else if name == "terrabase_request_duration_seconds_bucket" {
            &["region", "operation", "outcome", "le"]
        } else if matches!(name, "terrabase_replication_lag_seconds" | "terrabase_active_members") {
            &["region"]
        } else if matches!(name, "terrabase_storage_bytes" | "terrabase_storage_capacity_bytes") {
            &["tier"]
        } else if name == "terrabase_component_health" {
            &["component"]
        } else {
            panic!("unexpected labeled family {name}");
        };
        assert_eq!(keys, allowed, "label contract changed for {name}");
    }
}

#[test]
fn diagnostic_redaction_handles_structured_and_header_credentials() {
    let redacted = redact_diagnostic(
        r#"{"password":"db-secret","api-key":"key-secret"} Authorization: Bearer bearer-secret"#,
    );
    for secret in ["db-secret", "key-secret", "bearer-secret"] {
        assert!(!redacted.contains(secret));
    }
}

#[test]
fn healthy_core_remains_ready_when_optional_backup_is_degraded() {
    let health = terrabase::observability::HealthRegistry::new();
    for component in [
        Component::Storage,
        Component::Replication,
        Component::Membership,
        Component::ControlPlane,
        Component::Metrics,
    ] {
        health.set(component, HealthStatus::Healthy);
    }
    health.set(Component::Backup, HealthStatus::Degraded);
    let report = health.snapshot();
    assert!(report.is_ready());
    assert_eq!(report.health_status_code(), 200);
}

#[test]
fn admitted_data_plane_remains_ready_during_control_plane_loss() {
    let metrics = MetricsRegistry::new();
    for component in [Component::Storage, Component::Replication, Component::Membership] {
        metrics.set_component_health(component, HealthStatus::Healthy);
    }
    metrics.set_component_health(Component::ControlPlane, HealthStatus::Unhealthy);
    metrics
        .transition_service(ServiceState::Serving)
        .expect("the member was admitted before dependency loss");
    let snapshot = metrics.snapshot();
    assert!(snapshot.is_ready());
    assert_eq!(
        snapshot
            .component_health()
            .iter()
            .find(|health| health.component() == Component::ControlPlane)
            .expect("control-plane slot is present")
            .status(),
        HealthStatus::Unhealthy
    );
}

#[test]
fn storage_tier_metric_has_no_region_or_customer_labels() {
    let metrics = MetricsRegistry::new();
    metrics.set_storage_bytes(StorageTier::Tier2, 128);
    metrics.set_storage_capacity_bytes(StorageTier::Tier2, 512);
    let text = metrics.render_prometheus();
    assert!(text.contains("terrabase_storage_bytes{tier=\"tier2\"} 128"));
    assert!(text.contains("terrabase_storage_capacity_bytes{tier=\"tier2\"} 512"));
    assert!(!text.contains("region=\""));
    assert!(!text.contains("tenant=\""));
}
