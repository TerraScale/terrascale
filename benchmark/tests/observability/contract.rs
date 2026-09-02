//! Cross-module observability contract checks.
//!
//! Keeping these checks outside the implementation tests makes the adapter
//! contract visible and prevents a refactor from silently changing it.

use std::num::NonZeroU64;
use std::path::PathBuf;
use std::sync::Arc;
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::Duration;

use terrabase::observability::{
    AdmissionReason, BackupMode, CommitResult, Component, HealthStatus, MetricsRegistry, Operation,
    Outcome, PROMETHEUS_PATH, Queue, Region, ServiceState, StorageOperation, StorageTier,
    TaskOutcome, redact_diagnostic,
};
use terrabase::types::CollectionName;
use terrabase::usage::UsagePageRequest;
use terrabase::{
    CommitOutcome, Document, DocumentId, DocumentValue, Engine, EngineConfig, ExpectedVersion,
    LocalDurability, OperationId, TenantId,
};

fn engine_location(tag: &str) -> PathBuf {
    static COUNTER: AtomicU64 = AtomicU64::new(0);
    let index = COUNTER.fetch_add(1, Ordering::Relaxed);
    std::env::temp_dir()
        .join(format!("terrabase-observability-{tag}-{}-{index}", std::process::id()))
}

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
        "terrabase_in_flight_operations",
        "terrabase_admission_rejections_total",
        "terrabase_queue_depth",
        "terrabase_queue_capacity",
        "terrabase_wal_fsync_total",
        "terrabase_wal_fsync_duration_seconds",
        "terrabase_commits_total",
        "terrabase_commit_duration_seconds",
        "terrabase_replication_lag_seconds",
        "terrabase_replication_backlog_bytes",
        "terrabase_replication_frontier_health",
        "terrabase_storage_bytes",
        "terrabase_storage_capacity_bytes",
        "terrabase_storage_errors_total",
        "terrabase_active_members",
        "terrabase_component_health",
        "terrabase_compaction_backlog_bytes",
        "terrabase_compaction_runs_total",
        "terrabase_compaction_input_bytes_total",
        "terrabase_compaction_output_bytes_total",
        "terrabase_backup_last_success_age_seconds",
        "terrabase_backup_runs_total",
        "terrabase_usage_export_frontier",
        "terrabase_usage_export_failures_total",
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
fn production_diagnostic_signals_are_typed_and_share_the_snapshot() {
    let metrics = MetricsRegistry::new();
    metrics.set_in_flight(Operation::Write, 3);
    metrics.record_admission_rejection(Operation::Write, AdmissionReason::QueueFull);
    metrics.set_queue(Queue::Commit, 7, NonZeroU64::new(8).expect("test capacity is non-zero"));
    metrics.record_wal_fsync(TaskOutcome::Error, Duration::from_millis(20));
    metrics.record_commit(CommitResult::Unknown, Duration::from_millis(30));
    metrics.set_replication_backlog_bytes(Region::Fra, 1_024);
    metrics.set_replication_frontier_health(Region::Fra, HealthStatus::Degraded);
    metrics.record_storage_error(StorageTier::Tier2, StorageOperation::Flush);
    metrics.set_compaction_backlog_bytes(StorageTier::Tier2, 2_048);
    metrics.record_compaction(TaskOutcome::Success, 4_096, 3_072);
    metrics.set_backup_last_success_age(BackupMode::Continuous, Duration::from_secs(3_600));
    metrics.record_backup(BackupMode::Continuous, TaskOutcome::Error);
    assert!(metrics.advance_usage_export_frontier(42));
    assert!(!metrics.advance_usage_export_frontier(41));
    metrics.record_usage_export_failure();

    let snapshot = metrics.snapshot();
    assert_eq!(snapshot.in_flight(), &[(Operation::Write, 3)]);
    assert_eq!(
        snapshot.admission_rejections(),
        &[(Operation::Write, AdmissionReason::QueueFull, 1)]
    );
    assert_eq!(snapshot.queues(), &[(Queue::Commit, 7, 8)]);
    assert_eq!(snapshot.wal_fsync()[0].1, 1);
    assert_eq!(snapshot.wal_fsync()[0].2.count(), 1);
    assert_eq!(snapshot.commits()[0].0, CommitResult::Unknown);
    assert_eq!(snapshot.replication_backlog(), &[(Region::Fra, 1_024)]);
    assert_eq!(snapshot.replication_frontier_health(), &[(Region::Fra, HealthStatus::Degraded)]);
    assert_eq!(snapshot.storage_errors(), &[(StorageTier::Tier2, StorageOperation::Flush, 1)]);
    assert_eq!(snapshot.compaction_backlog()[0].bytes(), 2_048);
    assert_eq!(snapshot.compaction_runs(), &[(TaskOutcome::Success, 1)]);
    assert_eq!(snapshot.compaction_input_bytes(), 4_096);
    assert_eq!(snapshot.compaction_output_bytes(), 3_072);
    assert_eq!(snapshot.backup_last_success_age(), &[(BackupMode::Continuous, 3_600.0)]);
    assert_eq!(snapshot.backup_runs(), &[(BackupMode::Continuous, TaskOutcome::Error, 1)]);
    assert_eq!(snapshot.usage_export_frontier(), Some(42));
    assert_eq!(snapshot.usage_export_failures(), 1);

    let text = snapshot.render_prometheus();
    assert!(text.contains("terrabase_wal_fsync_total{outcome=\"error\"} 1"));
    assert!(text.contains("terrabase_commits_total{outcome=\"unknown\"} 1"));
    assert!(text.contains("terrabase_usage_export_frontier 42"));
}

#[test]
fn real_engine_commit_wal_and_usage_export_share_one_registry() {
    let location = engine_location("real-hooks");
    let engine = Engine::open(EngineConfig::durable(&location).expect("durable configuration"))
        .expect("engine opens");
    let metrics = engine.metrics();
    assert!(Arc::ptr_eq(&metrics, &engine.metrics()));

    let tenant = TenantId::try_new("tenant-a").expect("valid tenant");
    let scope = engine.scope_for(&tenant);
    let snapshot = engine.read_snapshot(&scope).expect("read snapshot");
    let operation = OperationId::try_new("obs-commit-1").expect("valid operation identity");
    let mut transaction = engine.begin(&scope, snapshot, operation).expect("transaction begins");
    transaction
        .put(
            Document::new(
                DocumentId::try_new("doc-1").expect("valid document identity"),
                CollectionName::try_new("users").expect("valid collection"),
                DocumentValue::String("observed".to_owned()),
            ),
            ExpectedVersion::New,
        )
        .expect("write stages");
    assert!(matches!(transaction.commit(LocalDurability::Synced), CommitOutcome::Committed(_)));

    let committed = metrics.snapshot();
    assert!(committed.commits().iter().any(|(result, total, histogram)| {
        *result == CommitResult::Committed && *total == 1 && histogram.count() == 1
    }));
    // The `wal_fsync` family is emitted by the commit path's own fsync
    // witness; the `SQLite` storage fsyncs inside its transaction, so the
    // family stays empty here and the M1 commit-path lane re-pins it.

    let page = engine
        .usage_page(
            &scope,
            UsagePageRequest::try_new(None, 16, None).expect("valid usage page request"),
        )
        .expect("usage page exports");
    assert_eq!(metrics.snapshot().usage_export_frontier(), Some(page.snapshot_sequence().get()));

    let other_location = engine_location("foreign-scope");
    let other =
        Engine::open(EngineConfig::durable(&other_location).expect("other durable configuration"))
            .expect("other engine opens");
    let foreign_scope = other.scope_for(&tenant);
    assert!(
        engine
            .usage_page(
                &foreign_scope,
                UsagePageRequest::try_new(None, 16, None).expect("valid usage page request"),
            )
            .is_err()
    );
    assert_eq!(metrics.snapshot().usage_export_failures(), 1);

    drop(other);
    drop(engine);
    let _ = std::fs::remove_dir_all(other_location);
    let _ = std::fs::remove_dir_all(location);
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
    metrics.set_replication_backlog_bytes(Region::Gru, 0);
    metrics.set_replication_frontier_health(Region::Gru, HealthStatus::Healthy);
    metrics.set_storage_bytes(StorageTier::Tier1, 0);
    metrics.set_storage_capacity_bytes(StorageTier::Tier1, 1);
    metrics.record_storage_error(StorageTier::Tier1, StorageOperation::Read);
    metrics.set_active_members(Region::Gru, 2);
    metrics.set_in_flight(Operation::Read, 1);
    metrics.record_admission_rejection(Operation::Read, AdmissionReason::Capacity);
    metrics.set_queue(Queue::Foreground, 0, NonZeroU64::new(1).expect("test capacity is non-zero"));
    metrics.record_wal_fsync(TaskOutcome::Success, Duration::ZERO);
    metrics.record_commit(CommitResult::Committed, Duration::ZERO);
    metrics.set_compaction_backlog_bytes(StorageTier::Tier1, 0);
    metrics.record_compaction(TaskOutcome::Success, 0, 0);
    metrics.set_backup_last_success_age(BackupMode::Full, Duration::ZERO);
    metrics.record_backup(BackupMode::Full, TaskOutcome::Success);

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
        } else if name == "terrabase_in_flight_operations" {
            &["operation"]
        } else if name == "terrabase_admission_rejections_total" {
            &["operation", "reason"]
        } else if matches!(name, "terrabase_queue_depth" | "terrabase_queue_capacity") {
            &["queue"]
        } else if matches!(
            name,
            "terrabase_wal_fsync_total"
                | "terrabase_commits_total"
                | "terrabase_wal_fsync_duration_seconds_sum"
                | "terrabase_wal_fsync_duration_seconds_count"
                | "terrabase_commit_duration_seconds_sum"
                | "terrabase_commit_duration_seconds_count"
        ) {
            &["outcome"]
        } else if matches!(
            name,
            "terrabase_wal_fsync_duration_seconds_bucket"
                | "terrabase_commit_duration_seconds_bucket"
        ) {
            &["outcome", "le"]
        } else if matches!(
            name,
            "terrabase_replication_lag_seconds"
                | "terrabase_active_members"
                | "terrabase_replication_backlog_bytes"
                | "terrabase_replication_frontier_health"
        ) {
            &["region"]
        } else if matches!(
            name,
            "terrabase_storage_bytes"
                | "terrabase_storage_capacity_bytes"
                | "terrabase_compaction_backlog_bytes"
        ) {
            &["tier"]
        } else if name == "terrabase_storage_errors_total" {
            &["tier", "operation"]
        } else if name == "terrabase_component_health" {
            &["component"]
        } else if name == "terrabase_compaction_runs_total" {
            &["outcome"]
        } else if name == "terrabase_backup_last_success_age_seconds" {
            &["mode"]
        } else if name == "terrabase_backup_runs_total" {
            &["mode", "outcome"]
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
fn unconfigured_optional_components_are_unknown_not_unhealthy() {
    let text = MetricsRegistry::new().render_prometheus();
    assert!(text.contains("terrabase_component_health{component=\"backup\"} -1"));
    assert!(text.contains("terrabase_component_health{component=\"metrics\"} -1"));
}

#[test]
fn alert_rules_preserve_required_optional_and_selected_region_semantics() {
    let rules = include_str!("../../deploy/observability/prometheus-rules.yml");
    assert!(rules.contains(
        r#"terrabase_component_health{component=~"storage|replication|membership"} <= 0"#
    ));
    assert!(
        rules.contains(
            r#"terrabase_component_health{component=~"control_plane|backup|metrics"} == 0"#
        )
    );
    assert!(!rules.contains("expr: terrabase_component_health == 0\n"));
    assert!(rules.contains(r#"terrabase_active_members{region!="unknown"} < 2"#));
    assert!(rules.contains("0.000000001"));
    assert!(!rules.contains("clamp_min(sum(rate(terrabase_requests_total[5m])), 1)"));
    for target_grouping in [
        "sum by (job, instance, le, region, operation)",
        "sum by (job, instance, operation, reason)",
        "sum by (job, instance, tier, operation)",
        "sum by (job, instance, mode)",
    ] {
        assert!(rules.contains(target_grouping), "alert dropped target labels: {target_grouping}");
    }

    for alert in [
        "TerraBaseQueueSaturationHigh",
        "TerraBaseWalFsyncErrors",
        "TerraBaseCommitOutcomeUnknown",
        "TerraBaseStorageErrors",
        "TerraBaseReplicationFrontierUnhealthy",
        "TerraBaseContinuousBackupStale",
        "TerraBaseUsageExportErrors",
    ] {
        assert_eq!(
            rules.matches(&format!("alert: {alert}")).count(),
            1,
            "alert {alert} must appear once"
        );
    }
}

#[test]
fn dashboard_includes_the_authoritative_usage_frontier() {
    let dashboard = include_str!("../../deploy/observability/grafana-dashboard.json");
    assert!(dashboard.contains("terrabase_usage_export_frontier"));
    assert!(dashboard.contains("terrabase_usage_export_failures_total"));
}

#[test]
fn prometheus_scrape_config_matches_rust_server_and_plaintext_boundary() {
    let config = include_str!("../../deploy/observability/prometheus.yml");
    assert!(config.contains(&format!("metrics_path: {PROMETHEUS_PATH}")));
    assert!(config.contains("scheme: http"));
    for forbidden in ["scheme: https", "tls_config:", "insecure_skip_verify"] {
        assert!(!config.contains(forbidden), "private plaintext scrape configured {forbidden}");
    }
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
