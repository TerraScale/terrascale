//! Dependency-free observability primitives for the `TerraBase` engine.
//!
//! This module owns the metric model and immutable snapshot used by the private
//! `terrabase-server` metrics and probe routes. It does not open a socket or
//! authenticate a caller. The server owns HTTP routing; `ts-orchestrator` owns
//! target discovery, workload authentication, and network policy.
//!
//! Metric labels are closed Rust enums rather than strings.  Consequently a
//! tenant ID, document ID, request ID, provider OCID, or error text cannot
//! accidentally become a Prometheus label.  `render_prometheus` emits the
//! Prometheus 0.0.4 text format with deterministic ordering and no timestamps.

use std::collections::BTreeMap;
use std::fmt::Write as _;
use std::num::NonZeroU64;
use std::sync::{Mutex, MutexGuard};
use std::time::Duration;

/// The content type required by the Prometheus text exposition format.
pub const PROMETHEUS_CONTENT_TYPE: &str = "text/plain; version=0.0.4; charset=utf-8";

/// The private production Prometheus endpoint path.
pub const PROMETHEUS_PATH: &str = "/internal/metrics";

/// The private liveness probe path.
pub const LIVENESS_PATH: &str = "/live";

/// The private startup probe path.
pub const STARTUP_PATH: &str = "/startup";

/// The private readiness probe path.
pub const READINESS_PATH: &str = "/ready";

/// The fixed upper bound on the number of metric label values.
///
/// This is a documentation and review guard.  The actual bound is enforced by
/// the closed enums below, not by accepting arbitrary label values and
/// truncating them after the fact.
pub const MAX_REGION_LABEL_VALUES: usize = REGIONS.len();

/// The fixed upper bound on operation label values.
pub const MAX_OPERATION_LABEL_VALUES: usize = OPERATIONS.len();

/// The fixed upper bound on outcome label values.
pub const MAX_OUTCOME_LABEL_VALUES: usize = OUTCOMES.len();

/// The fixed upper bound on storage-tier label values.
pub const MAX_STORAGE_TIER_LABEL_VALUES: usize = STORAGE_TIERS.len();

/// The fixed upper bound on component label values.
pub const MAX_COMPONENT_LABEL_VALUES: usize = COMPONENTS.len();

/// The maximum number of request counter and histogram series.
///
/// Each series is one member of the closed Region, operation, and outcome
/// product. No runtime string can increase this value.
pub const MAX_REQUEST_SERIES: usize =
    MAX_REGION_LABEL_VALUES * MAX_OPERATION_LABEL_VALUES * MAX_OUTCOME_LABEL_VALUES;

/// The maximum accepted size of one Prometheus exposition body.
///
/// The closed metric and label registries keep a fully populated body below
/// this limit. Adapters must reject an oversized body instead of sending a
/// partial exposition.
pub const MAX_PROMETHEUS_BODY_BYTES: usize = 512 * 1024;

/// The maximum diagnostic text inspected by [`redact_diagnostic`].
///
/// Diagnostic text is not a metric label and is therefore intentionally kept
/// separate from the exposition bound.  Truncating before classification gives
/// callers a fixed-cost disclosure boundary even when a dependency supplies a
/// pathological error string.
pub const MAX_DIAGNOSTIC_BYTES: usize = 64 * 1024;

/// The fixed histogram boundaries for request latency, in seconds.
pub const REQUEST_LATENCY_BUCKETS: [f64; 11] =
    [0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1.0, 2.5, 5.0, 10.0];

/// A bounded operation family used by request metrics.
#[derive(Clone, Copy, Debug, Eq, Ord, PartialEq, PartialOrd)]
pub enum Operation {
    /// A point or range read.
    Read,
    /// A mutation or transaction commit.
    Write,
    /// A query or query page.
    Query,
    /// A reactive subscription operation.
    Subscribe,
    /// A backup operation.
    Backup,
    /// A restore operation.
    Restore,
    /// A repair operation.
    Repair,
    /// A compaction operation.
    Compaction,
    /// An operation not yet assigned a more specific family.
    Other,
}

impl Operation {
    /// Returns the stable label value for this operation family.
    #[must_use]
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::Read => "read",
            Self::Write => "write",
            Self::Query => "query",
            Self::Subscribe => "subscribe",
            Self::Backup => "backup",
            Self::Restore => "restore",
            Self::Repair => "repair",
            Self::Compaction => "compaction",
            Self::Other => "other",
        }
    }
}

/// Every admitted operation label in stable order.
pub const OPERATIONS: [Operation; 9] = [
    Operation::Read,
    Operation::Write,
    Operation::Query,
    Operation::Subscribe,
    Operation::Backup,
    Operation::Restore,
    Operation::Repair,
    Operation::Compaction,
    Operation::Other,
];

/// The bounded result class used by request metrics.
#[derive(Clone, Copy, Debug, Eq, Ord, PartialEq, PartialOrd)]
pub enum Outcome {
    /// The operation completed according to its contract.
    Success,
    /// `TerraBase` or a dependency returned an error.
    Error,
    /// Admission, authentication, validation, or capacity rejected it.
    Rejected,
    /// The operation exceeded its deadline.
    Timeout,
}

impl Outcome {
    /// Returns the stable label value for this outcome class.
    #[must_use]
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::Success => "success",
            Self::Error => "error",
            Self::Rejected => "rejected",
            Self::Timeout => "timeout",
        }
    }
}

/// Every admitted outcome label in stable order.
pub const OUTCOMES: [Outcome; 4] =
    [Outcome::Success, Outcome::Error, Outcome::Rejected, Outcome::Timeout];

/// A `TerraScale` Region label.
///
/// The three initial Regions are the only named values.  `Unknown` is an
/// aggregate for startup, migration, or incomplete topology observations; it
/// prevents an untrusted provider or user string from entering a label.
#[derive(Clone, Copy, Debug, Eq, Ord, PartialEq, PartialOrd)]
pub enum Region {
    /// São Paulo.
    Gru,
    /// San Francisco.
    Sfo,
    /// Frankfurt.
    Fra,
    /// No admitted Region is known for the observation.
    Unknown,
}

impl Region {
    /// Returns the stable uppercase airport-code label.
    #[must_use]
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::Gru => "GRU",
            Self::Sfo => "SFO",
            Self::Fra => "FRA",
            Self::Unknown => "unknown",
        }
    }

    /// Maps an exact `TerraScale` `RegionCode` to a bounded metric label.
    ///
    /// Inputs are intentionally not normalized: aliases, lowercase values,
    /// OCI names, and malformed values become `Unknown`.
    #[must_use]
    pub fn from_code(code: &str) -> Self {
        match code {
            "GRU" => Self::Gru,
            "SFO" => Self::Sfo,
            "FRA" => Self::Fra,
            _ => Self::Unknown,
        }
    }
}

/// Every admitted Region label in stable order.
pub const REGIONS: [Region; 4] = [Region::Gru, Region::Sfo, Region::Fra, Region::Unknown];

/// The bounded storage tiers reported by the storage observer.
#[derive(Clone, Copy, Debug, Eq, Ord, PartialEq, PartialOrd)]
pub enum StorageTier {
    /// Fast local storage, when configured.
    Tier1,
    /// Required block-storage tier.
    Tier2,
    /// Optional object-storage tier.
    Tier3,
}

impl StorageTier {
    /// Returns the stable label value for this tier.
    #[must_use]
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::Tier1 => "tier1",
            Self::Tier2 => "tier2",
            Self::Tier3 => "tier3",
        }
    }
}

/// Every admitted storage-tier label in stable order.
pub const STORAGE_TIERS: [StorageTier; 3] =
    [StorageTier::Tier1, StorageTier::Tier2, StorageTier::Tier3];

/// A bounded admission refusal reason.
#[derive(Clone, Copy, Debug, Eq, Ord, PartialEq, PartialOrd)]
pub enum AdmissionReason {
    /// The configured storage or memory budget was exhausted.
    Capacity,
    /// The concurrency limit was reached.
    Concurrency,
    /// The bounded queue had no available slot.
    QueueFull,
    /// The request deadline expired before admission.
    Deadline,
    /// The service was starting, draining, or stopped.
    NotServing,
    /// A refusal that cannot be assigned to another bounded class.
    Other,
}

impl AdmissionReason {
    /// Returns the stable label value.
    #[must_use]
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::Capacity => "capacity",
            Self::Concurrency => "concurrency",
            Self::QueueFull => "queue_full",
            Self::Deadline => "deadline",
            Self::NotServing => "not_serving",
            Self::Other => "other",
        }
    }
}

/// Every admitted admission-reason label in stable order.
pub const ADMISSION_REASONS: [AdmissionReason; 6] = [
    AdmissionReason::Capacity,
    AdmissionReason::Concurrency,
    AdmissionReason::QueueFull,
    AdmissionReason::Deadline,
    AdmissionReason::NotServing,
    AdmissionReason::Other,
];

/// A bounded internal work queue.
#[derive(Clone, Copy, Debug, Eq, Ord, PartialEq, PartialOrd)]
pub enum Queue {
    /// Foreground request admission.
    Foreground,
    /// Group commit and WAL synchronization.
    Commit,
    /// Replication and repair delivery.
    Replication,
    /// Background compaction.
    Compaction,
    /// Backup production or verification.
    Backup,
    /// Authoritative usage-fact export.
    UsageExport,
}

impl Queue {
    /// Returns the stable label value.
    #[must_use]
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::Foreground => "foreground",
            Self::Commit => "commit",
            Self::Replication => "replication",
            Self::Compaction => "compaction",
            Self::Backup => "backup",
            Self::UsageExport => "usage_export",
        }
    }
}

/// Every admitted queue label in stable order.
pub const QUEUES: [Queue; 6] = [
    Queue::Foreground,
    Queue::Commit,
    Queue::Replication,
    Queue::Compaction,
    Queue::Backup,
    Queue::UsageExport,
];

/// The result of one bounded background or I/O task.
#[derive(Clone, Copy, Debug, Eq, Ord, PartialEq, PartialOrd)]
pub enum TaskOutcome {
    /// The task completed successfully.
    Success,
    /// The task failed.
    Error,
}

impl TaskOutcome {
    /// Returns the stable label value.
    #[must_use]
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::Success => "success",
            Self::Error => "error",
        }
    }
}

/// Every admitted background-task outcome label in stable order.
pub const TASK_OUTCOMES: [TaskOutcome; 2] = [TaskOutcome::Success, TaskOutcome::Error];

/// The durability result of one commit attempt.
#[derive(Clone, Copy, Debug, Eq, Ord, PartialEq, PartialOrd)]
pub enum CommitResult {
    /// The requested durability contract was satisfied.
    Committed,
    /// Admission or validation refused the commit before it began.
    Refused,
    /// The commit failed with a known non-durable outcome.
    Failed,
    /// The caller must resolve an indeterminate durability outcome.
    Unknown,
}

impl CommitResult {
    /// Returns the stable label value.
    #[must_use]
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::Committed => "committed",
            Self::Refused => "refused",
            Self::Failed => "failed",
            Self::Unknown => "unknown",
        }
    }
}

/// Every admitted commit-result label in stable order.
pub const COMMIT_RESULTS: [CommitResult; 4] =
    [CommitResult::Committed, CommitResult::Refused, CommitResult::Failed, CommitResult::Unknown];

/// A bounded backup mode.
#[derive(Clone, Copy, Debug, Eq, Ord, PartialEq, PartialOrd)]
pub enum BackupMode {
    /// A full backup root.
    Full,
    /// An incremental backup linked to a prior root.
    Incremental,
    /// Continuous immutable backup delivery.
    Continuous,
}

impl BackupMode {
    /// Returns the stable label value.
    #[must_use]
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::Full => "full",
            Self::Incremental => "incremental",
            Self::Continuous => "continuous",
        }
    }
}

/// Every admitted backup-mode label in stable order.
pub const BACKUP_MODES: [BackupMode; 3] =
    [BackupMode::Full, BackupMode::Incremental, BackupMode::Continuous];

/// A bounded storage operation used to classify storage errors.
#[derive(Clone, Copy, Debug, Eq, Ord, PartialEq, PartialOrd)]
pub enum StorageOperation {
    /// A storage read.
    Read,
    /// A storage write.
    Write,
    /// A WAL or segment flush.
    Flush,
    /// A compaction operation.
    Compaction,
    /// An operation that cannot be assigned to another bounded class.
    Other,
}

impl StorageOperation {
    /// Returns the stable label value.
    #[must_use]
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::Read => "read",
            Self::Write => "write",
            Self::Flush => "flush",
            Self::Compaction => "compaction",
            Self::Other => "other",
        }
    }
}

/// Every admitted storage-operation label in stable order.
pub const STORAGE_OPERATIONS: [StorageOperation; 5] = [
    StorageOperation::Read,
    StorageOperation::Write,
    StorageOperation::Flush,
    StorageOperation::Compaction,
    StorageOperation::Other,
];

/// A bounded engine component that can participate in health checks.
#[derive(Clone, Copy, Debug, Eq, Ord, PartialEq, PartialOrd)]
pub enum Component {
    /// WAL, storage, and filesystem state.
    Storage,
    /// Cross-Region replication and repair links.
    Replication,
    /// Member and Region admission state.
    Membership,
    /// The private control-plane dependency.
    ControlPlane,
    /// Backup destination and verification worker.
    Backup,
    /// Internal metrics collection.
    Metrics,
}

impl Component {
    /// Returns the stable label value for this component.
    #[must_use]
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::Storage => "storage",
            Self::Replication => "replication",
            Self::Membership => "membership",
            Self::ControlPlane => "control_plane",
            Self::Backup => "backup",
            Self::Metrics => "metrics",
        }
    }

    /// Whether this component must be healthy before the process is ready.
    #[must_use]
    pub const fn required_for_readiness(self) -> bool {
        matches!(self, Self::Storage | Self::Replication | Self::Membership)
    }
}

/// Components that must be healthy before the process can accept traffic.
///
/// The control plane is needed for admission and control mutations, but an
/// admitted member keeps serving reads and converging data when that dependency
/// is unavailable. The lifecycle state owns the initial admission gate, while
/// component health keeps the loss visible to operators.
pub const REQUIRED_COMPONENTS: [Component; 3] =
    [Component::Storage, Component::Replication, Component::Membership];

/// The bounded service lifecycle used to gate readiness.
#[derive(Clone, Copy, Debug, Default, Eq, Ord, PartialEq, PartialOrd)]
pub enum ServiceState {
    /// The process has started but has not completed admission.
    #[default]
    Starting,
    /// The process may accept traffic when required components are healthy.
    Serving,
    /// The process is alive but has withdrawn from new traffic.
    Draining,
    /// The engine has stopped serving.
    Stopped,
}

impl ServiceState {
    /// Returns the stable state name used by structured operator APIs.
    #[must_use]
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::Starting => "starting",
            Self::Serving => "serving",
            Self::Draining => "draining",
            Self::Stopped => "stopped",
        }
    }

    /// Whether this lifecycle state permits readiness.
    #[must_use]
    pub const fn permits_readiness(self) -> bool {
        matches!(self, Self::Serving)
    }

    /// Whether the process lifecycle is non-terminal.
    #[must_use]
    pub const fn is_live(self) -> bool {
        !matches!(self, Self::Stopped)
    }

    fn permits_transition_to(self, next: Self) -> bool {
        self == next
            || matches!(
                (self, next),
                (Self::Starting, Self::Serving | Self::Draining | Self::Stopped)
                    | (Self::Serving, Self::Draining | Self::Stopped)
                    | (Self::Draining, Self::Stopped)
            )
    }
}

/// A refused backwards or post-stop service lifecycle transition.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct ServiceTransitionError {
    from: ServiceState,
    to: ServiceState,
}

impl ServiceTransitionError {
    /// Returns the state at the time of the refusal.
    #[must_use]
    pub const fn from(&self) -> ServiceState {
        self.from
    }

    /// Returns the refused target state.
    #[must_use]
    pub const fn to(&self) -> ServiceState {
        self.to
    }
}

impl std::fmt::Display for ServiceTransitionError {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(
            formatter,
            "service state cannot move from {} to {}",
            self.from.as_str(),
            self.to.as_str()
        )
    }
}

impl std::error::Error for ServiceTransitionError {}

/// A refusal to serve a Prometheus body beyond the contract limit.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct ExpositionTooLarge {
    bytes: usize,
}

impl ExpositionTooLarge {
    /// Returns the rendered size that exceeded the limit.
    #[must_use]
    pub const fn bytes(&self) -> usize {
        self.bytes
    }

    /// Returns the fixed response-size limit.
    #[must_use]
    pub const fn limit(&self) -> usize {
        MAX_PROMETHEUS_BODY_BYTES
    }
}

impl std::fmt::Display for ExpositionTooLarge {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(
            formatter,
            "Prometheus body is {} bytes; limit is {} bytes",
            self.bytes, MAX_PROMETHEUS_BODY_BYTES
        )
    }
}

impl std::error::Error for ExpositionTooLarge {}

/// The state of one health check.
#[derive(Clone, Copy, Debug, Eq, Ord, PartialEq, PartialOrd)]
pub enum HealthStatus {
    /// The component is operating normally.
    Healthy,
    /// The component is serving but an SLO or redundancy margin is reduced.
    Degraded,
    /// The component cannot support its contract.
    Unhealthy,
    /// No trustworthy observation has been made.
    Unknown,
}

impl HealthStatus {
    /// Returns the numeric gauge value used by `terrabase_component_health`.
    ///
    /// A gauge is used instead of a status label so a state transition cannot
    /// leave stale one-series-per-state data behind in a scrape store.
    #[must_use]
    pub const fn gauge_value(self) -> f64 {
        match self {
            Self::Healthy => 1.0,
            Self::Degraded => 0.5,
            Self::Unhealthy => 0.0,
            Self::Unknown => -1.0,
        }
    }

    /// Whether this status is safe for readiness.
    #[must_use]
    pub const fn is_ready(self) -> bool {
        matches!(self, Self::Healthy)
    }

    /// Returns a stable textual state for diagnostics.
    #[must_use]
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::Healthy => "healthy",
            Self::Degraded => "degraded",
            Self::Unhealthy => "unhealthy",
            Self::Unknown => "unknown",
        }
    }
}

/// One immutable health observation.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct HealthCheck {
    /// The observed component.
    component: Component,
    /// The current component state.
    status: HealthStatus,
}

impl HealthCheck {
    /// Returns the observed component.
    #[must_use]
    pub const fn component(&self) -> Component {
        self.component
    }

    /// Returns the observed state.
    #[must_use]
    pub const fn status(&self) -> HealthStatus {
        self.status
    }
}

/// An immutable process health result.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct HealthReport {
    /// The aggregate state of all observed components.
    status: HealthStatus,
    /// One entry for every bounded component, in contract order.
    checks: Vec<HealthCheck>,
}

impl HealthReport {
    /// Returns the aggregate component status.
    #[must_use]
    pub const fn status(&self) -> HealthStatus {
        self.status
    }

    /// Returns every component check in contract order.
    #[must_use]
    pub fn checks(&self) -> &[HealthCheck] {
        &self.checks
    }

    /// Returns true only when each required component appears once and is
    /// healthy.
    #[must_use]
    pub fn is_ready(&self) -> bool {
        REQUIRED_COMPONENTS.iter().all(|required| {
            let mut matching = self.checks.iter().filter(|check| check.component == *required);
            matches!(matching.next(), Some(check) if check.status.is_ready())
                && matching.next().is_none()
        })
    }

    /// Returns the status code an adapter should use for a readiness probe.
    #[must_use]
    pub fn readiness_status_code(&self) -> u16 {
        if self.is_ready() {
            200
        } else {
            503
        }
    }

    /// Returns the status code an adapter should use for a health probe.
    #[must_use]
    pub fn health_status_code(&self) -> u16 {
        if matches!(self.status, HealthStatus::Unhealthy | HealthStatus::Unknown) {
            503
        } else {
            200
        }
    }
}

/// Process-local health state, independent from metric request counting.
pub struct HealthRegistry {
    checks: Mutex<BTreeMap<Component, HealthStatus>>,
}

impl Default for HealthRegistry {
    fn default() -> Self {
        Self::new()
    }
}

impl HealthRegistry {
    /// Creates an empty registry.  Missing required checks are not ready.
    #[must_use]
    pub fn new() -> Self {
        Self { checks: Mutex::new(BTreeMap::new()) }
    }

    /// Records a component state using only a bounded component value.
    pub fn set(&self, component: Component, status: HealthStatus) {
        lock(&self.checks).insert(component, status);
    }

    /// Takes a stable snapshot of all six component slots.
    #[must_use]
    pub fn snapshot(&self) -> HealthReport {
        let checks = lock(&self.checks);
        let mut entries = Vec::with_capacity(6);
        for component in COMPONENTS {
            entries.push(HealthCheck {
                component,
                status: checks.get(&component).copied().unwrap_or(HealthStatus::Unknown),
            });
        }
        let status = aggregate_health(&entries);
        HealthReport { status, checks: entries }
    }

    /// Returns the aggregate health view for a health-probe adapter.
    #[must_use]
    pub fn health(&self) -> HealthReport {
        self.snapshot()
    }

    /// Returns readiness independently from the process-level liveness view.
    #[must_use]
    pub fn readiness(&self) -> bool {
        self.snapshot().is_ready()
    }

    /// Returns the liveness result.  Liveness is intentionally process-level:
    /// a failed dependency affects readiness, not whether this process exists.
    #[must_use]
    pub const fn liveness(&self) -> bool {
        true
    }
}

/// The fixed component order used in reports and exposition.
pub const COMPONENTS: [Component; 6] = [
    Component::Storage,
    Component::Replication,
    Component::Membership,
    Component::ControlPlane,
    Component::Backup,
    Component::Metrics,
];

fn aggregate_health(checks: &[HealthCheck]) -> HealthStatus {
    if checks.is_empty() || checks.iter().any(|check| check.status == HealthStatus::Unhealthy) {
        HealthStatus::Unhealthy
    } else if checks.iter().any(|check| check.status == HealthStatus::Unknown) {
        HealthStatus::Unknown
    } else if checks.iter().any(|check| check.status == HealthStatus::Degraded) {
        HealthStatus::Degraded
    } else {
        HealthStatus::Healthy
    }
}

#[derive(Clone, Copy, Debug, Eq, Ord, PartialEq, PartialOrd)]
struct RequestKey {
    region: Region,
    operation: Operation,
    outcome: Outcome,
}

#[derive(Clone, Copy, Debug, Default)]
struct Histogram {
    buckets: [u64; REQUEST_LATENCY_BUCKETS.len()],
    count: u64,
    sum_seconds: f64,
}

impl Histogram {
    fn observe(&mut self, duration: Duration) {
        let seconds = duration.as_secs_f64();
        self.count = self.count.saturating_add(1);
        self.sum_seconds += seconds;
        for (index, upper_bound) in REQUEST_LATENCY_BUCKETS.iter().enumerate() {
            if seconds <= *upper_bound {
                self.buckets[index] = self.buckets[index].saturating_add(1);
                break;
            }
        }
    }
}

/// An immutable fixed-bucket request latency snapshot.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct HistogramSnapshot {
    cumulative_buckets: [u64; REQUEST_LATENCY_BUCKETS.len()],
    count: u64,
    sum_seconds: f64,
}

impl HistogramSnapshot {
    fn from_histogram(histogram: Histogram) -> Self {
        let mut cumulative_buckets = histogram.buckets;
        for index in 1..cumulative_buckets.len() {
            cumulative_buckets[index] =
                cumulative_buckets[index].saturating_add(cumulative_buckets[index - 1]);
        }
        Self { cumulative_buckets, count: histogram.count, sum_seconds: histogram.sum_seconds }
    }

    /// Returns cumulative counts paired by index with
    /// [`REQUEST_LATENCY_BUCKETS`].
    #[must_use]
    pub const fn cumulative_buckets(&self) -> &[u64; REQUEST_LATENCY_BUCKETS.len()] {
        &self.cumulative_buckets
    }

    /// Returns the number of observations, including values above the last
    /// finite bucket.
    #[must_use]
    pub const fn count(&self) -> u64 {
        self.count
    }

    /// Returns the sum of observed seconds.
    #[must_use]
    pub const fn sum_seconds(&self) -> f64 {
        self.sum_seconds
    }
}

/// One bounded request counter and latency series.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct RequestSeries {
    region: Region,
    operation: Operation,
    outcome: Outcome,
    total: u64,
    duration: HistogramSnapshot,
}

impl RequestSeries {
    /// Returns the Region label.
    #[must_use]
    pub const fn region(&self) -> Region {
        self.region
    }

    /// Returns the operation label.
    #[must_use]
    pub const fn operation(&self) -> Operation {
        self.operation
    }

    /// Returns the outcome label.
    #[must_use]
    pub const fn outcome(&self) -> Outcome {
        self.outcome
    }

    /// Returns the completed request count.
    #[must_use]
    pub const fn total(&self) -> u64 {
        self.total
    }

    /// Returns the fixed-bucket latency snapshot for the same requests.
    #[must_use]
    pub const fn duration(&self) -> &HistogramSnapshot {
        &self.duration
    }

    const fn key(self) -> RequestKey {
        RequestKey { region: self.region, operation: self.operation, outcome: self.outcome }
    }
}

/// One bounded Region's replication-lag observation.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct ReplicationLag {
    region: Region,
    seconds: f64,
}

impl ReplicationLag {
    /// Returns the Region label.
    #[must_use]
    pub const fn region(&self) -> Region {
        self.region
    }

    /// Returns lag in seconds.
    #[must_use]
    pub const fn seconds(&self) -> f64 {
        self.seconds
    }
}

/// One bounded storage tier's measured bytes.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct StorageUsage {
    tier: StorageTier,
    bytes: u64,
}

impl StorageUsage {
    /// Returns the storage tier label.
    #[must_use]
    pub const fn tier(&self) -> StorageTier {
        self.tier
    }

    /// Returns measured bytes.
    #[must_use]
    pub const fn bytes(&self) -> u64 {
        self.bytes
    }
}

/// One bounded storage tier's configured usable capacity.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct StorageCapacity {
    tier: StorageTier,
    bytes: u64,
}

impl StorageCapacity {
    /// Returns the storage tier label.
    #[must_use]
    pub const fn tier(&self) -> StorageTier {
        self.tier
    }

    /// Returns usable capacity in bytes.
    #[must_use]
    pub const fn bytes(&self) -> u64 {
        self.bytes
    }
}

/// One bounded Region's active member count.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct ActiveMembers {
    region: Region,
    members: u64,
}

impl ActiveMembers {
    /// Returns the Region label.
    #[must_use]
    pub const fn region(&self) -> Region {
        self.region
    }

    /// Returns the active member count.
    #[must_use]
    pub const fn members(&self) -> u64 {
        self.members
    }
}

/// One bounded component's current health.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct ComponentHealth {
    component: Component,
    status: HealthStatus,
}

impl ComponentHealth {
    /// Returns the component label.
    #[must_use]
    pub const fn component(&self) -> Component {
        self.component
    }

    /// Returns the current status.
    #[must_use]
    pub const fn status(&self) -> HealthStatus {
        self.status
    }
}

/// An immutable, bounded snapshot shared by private adapters and Prometheus.
///
/// The vectors can contain only closed enum values and each registry key occurs
/// at most once. This type carries no tenant, document, query, path, credential,
/// provider, or free-form diagnostic value.
#[derive(Clone, Debug, PartialEq)]
pub struct MetricsSnapshot {
    service_state: ServiceState,
    started: bool,
    ready: bool,
    requests: Vec<RequestSeries>,
    in_flight: Vec<(Operation, u64)>,
    admission_rejections: Vec<(Operation, AdmissionReason, u64)>,
    queues: Vec<(Queue, u64, u64)>,
    wal_fsync: Vec<(TaskOutcome, u64, HistogramSnapshot)>,
    commits: Vec<(CommitResult, u64, HistogramSnapshot)>,
    replication_lag: Vec<ReplicationLag>,
    replication_backlog: Vec<(Region, u64)>,
    replication_frontier_health: Vec<(Region, HealthStatus)>,
    storage: Vec<StorageUsage>,
    storage_capacity: Vec<StorageCapacity>,
    storage_errors: Vec<(StorageTier, StorageOperation, u64)>,
    active_members: Vec<ActiveMembers>,
    component_health: Vec<ComponentHealth>,
    compaction_backlog: Vec<StorageUsage>,
    compaction_runs: Vec<(TaskOutcome, u64)>,
    compaction_input_bytes: u64,
    compaction_output_bytes: u64,
    backup_last_success_age: Vec<(BackupMode, f64)>,
    backup_runs: Vec<(BackupMode, TaskOutcome, u64)>,
    usage_export_frontier: Option<u64>,
    usage_export_failures: u64,
}

impl MetricsSnapshot {
    /// Returns the lifecycle state captured with the metric values.
    #[must_use]
    pub const fn service_state(&self) -> ServiceState {
        self.service_state
    }

    /// Returns false only after a terminal process lifecycle transition.
    #[must_use]
    pub const fn is_live(&self) -> bool {
        self.service_state.is_live()
    }

    /// Returns whether the service reached the serving state. This fact is
    /// monotonic across draining and shutdown.
    #[must_use]
    pub const fn is_started(&self) -> bool {
        self.started
    }

    /// Returns whether the service was serving with every required component
    /// healthy.
    #[must_use]
    pub const fn is_ready(&self) -> bool {
        self.ready
    }

    /// Returns request series in stable label order.
    #[must_use]
    pub fn requests(&self) -> &[RequestSeries] {
        &self.requests
    }

    /// Returns `(operation, count)` in-flight gauges in stable operation order.
    #[must_use]
    pub fn in_flight(&self) -> &[(Operation, u64)] {
        &self.in_flight
    }

    /// Returns `(operation, reason, total)` admission rejection counters.
    #[must_use]
    pub fn admission_rejections(&self) -> &[(Operation, AdmissionReason, u64)] {
        &self.admission_rejections
    }

    /// Returns `(queue, depth, capacity)` observations in stable queue order.
    #[must_use]
    pub fn queues(&self) -> &[(Queue, u64, u64)] {
        &self.queues
    }

    /// Returns `(outcome, total, duration)` WAL fsync series.
    #[must_use]
    pub fn wal_fsync(&self) -> &[(TaskOutcome, u64, HistogramSnapshot)] {
        &self.wal_fsync
    }

    /// Returns `(result, total, duration)` commit series.
    #[must_use]
    pub fn commits(&self) -> &[(CommitResult, u64, HistogramSnapshot)] {
        &self.commits
    }

    /// Returns replication lag in stable Region order.
    #[must_use]
    pub fn replication_lag(&self) -> &[ReplicationLag] {
        &self.replication_lag
    }

    /// Returns `(region, bytes)` replication backlog gauges.
    #[must_use]
    pub fn replication_backlog(&self) -> &[(Region, u64)] {
        &self.replication_backlog
    }

    /// Returns `(region, status)` replication frontier observations.
    #[must_use]
    pub fn replication_frontier_health(&self) -> &[(Region, HealthStatus)] {
        &self.replication_frontier_health
    }

    /// Returns storage usage in stable tier order.
    #[must_use]
    pub fn storage(&self) -> &[StorageUsage] {
        &self.storage
    }

    /// Returns configured usable capacity in stable tier order.
    #[must_use]
    pub fn storage_capacity(&self) -> &[StorageCapacity] {
        &self.storage_capacity
    }

    /// Returns `(tier, operation, total)` storage error counters.
    #[must_use]
    pub fn storage_errors(&self) -> &[(StorageTier, StorageOperation, u64)] {
        &self.storage_errors
    }

    /// Returns active members in stable Region order.
    #[must_use]
    pub fn active_members(&self) -> &[ActiveMembers] {
        &self.active_members
    }

    /// Returns all component slots in contract order. Missing observations are
    /// represented as [`HealthStatus::Unknown`].
    #[must_use]
    pub fn component_health(&self) -> &[ComponentHealth] {
        &self.component_health
    }

    /// Returns compaction backlog bytes in stable tier order.
    #[must_use]
    pub fn compaction_backlog(&self) -> &[StorageUsage] {
        &self.compaction_backlog
    }

    /// Returns `(outcome, total)` compaction run counters.
    #[must_use]
    pub fn compaction_runs(&self) -> &[(TaskOutcome, u64)] {
        &self.compaction_runs
    }

    /// Returns total compaction input bytes observed by this process.
    #[must_use]
    pub const fn compaction_input_bytes(&self) -> u64 {
        self.compaction_input_bytes
    }

    /// Returns total compaction output bytes observed by this process.
    #[must_use]
    pub const fn compaction_output_bytes(&self) -> u64 {
        self.compaction_output_bytes
    }

    /// Returns `(mode, age_seconds)` last-success backup observations.
    #[must_use]
    pub fn backup_last_success_age(&self) -> &[(BackupMode, f64)] {
        &self.backup_last_success_age
    }

    /// Returns `(mode, outcome, total)` backup run counters.
    #[must_use]
    pub fn backup_runs(&self) -> &[(BackupMode, TaskOutcome, u64)] {
        &self.backup_runs
    }

    /// Returns the last exported authoritative usage frontier, when observed.
    #[must_use]
    pub const fn usage_export_frontier(&self) -> Option<u64> {
        self.usage_export_frontier
    }

    /// Returns usage-export failures observed by this process.
    #[must_use]
    pub const fn usage_export_failures(&self) -> u64 {
        self.usage_export_failures
    }

    /// Renders this immutable snapshot as Prometheus 0.0.4 text.
    #[must_use]
    pub fn render_prometheus(&self) -> String {
        let output = self.render_prometheus_unchecked();
        debug_assert!(output.len() <= MAX_PROMETHEUS_BODY_BYTES);
        output
    }

    /// Renders a bounded Prometheus body for a private network adapter.
    ///
    /// The current closed registry is statically bounded below the limit. This
    /// checked form keeps a future contract expansion from sending a partial or
    /// oversized response.
    ///
    /// # Errors
    ///
    /// Returns [`ExpositionTooLarge`] when the rendered body exceeds
    /// [`MAX_PROMETHEUS_BODY_BYTES`].
    pub fn try_render_prometheus(&self) -> Result<String, ExpositionTooLarge> {
        let output = self.render_prometheus_unchecked();
        if output.len() > MAX_PROMETHEUS_BODY_BYTES {
            Err(ExpositionTooLarge { bytes: output.len() })
        } else {
            Ok(output)
        }
    }

    fn render_prometheus_unchecked(&self) -> String {
        let mut output = String::new();
        write_process_metrics(&mut output, self.is_live(), self.started, self.ready);
        write_request_metrics(&mut output, self);
        write_admission_metrics(&mut output, self);
        write_durability_metrics(&mut output, self);
        write_resource_metrics(&mut output, self);
        write_background_metrics(&mut output, self);
        write_health_metrics(&mut output, self);
        output
    }
}

#[derive(Default)]
struct MetricsState {
    request_totals: BTreeMap<RequestKey, u64>,
    request_durations: BTreeMap<RequestKey, Histogram>,
    in_flight: BTreeMap<Operation, u64>,
    admission_rejections: BTreeMap<(Operation, AdmissionReason), u64>,
    queues: BTreeMap<Queue, (u64, u64)>,
    wal_fsync: BTreeMap<TaskOutcome, (u64, Histogram)>,
    commits: BTreeMap<CommitResult, (u64, Histogram)>,
    replication_lag_seconds: BTreeMap<Region, f64>,
    replication_backlog_bytes: BTreeMap<Region, u64>,
    replication_frontier_health: BTreeMap<Region, HealthStatus>,
    storage_bytes: BTreeMap<StorageTier, u64>,
    storage_capacity_bytes: BTreeMap<StorageTier, u64>,
    storage_errors: BTreeMap<(StorageTier, StorageOperation), u64>,
    active_members: BTreeMap<Region, u64>,
    component_health: BTreeMap<Component, HealthStatus>,
    compaction_backlog_bytes: BTreeMap<StorageTier, u64>,
    compaction_runs: BTreeMap<TaskOutcome, u64>,
    compaction_input_bytes: u64,
    compaction_output_bytes: u64,
    backup_last_success_age_seconds: BTreeMap<BackupMode, f64>,
    backup_runs: BTreeMap<(BackupMode, TaskOutcome), u64>,
    usage_export_frontier: Option<u64>,
    usage_export_failures: u64,
    service_state: ServiceState,
    started: bool,
    health_ready: bool,
}

fn required_health_is_ready(health: &BTreeMap<Component, HealthStatus>) -> bool {
    REQUIRED_COMPONENTS
        .iter()
        .all(|component| health.get(component).is_some_and(|status| status.is_ready()))
}

/// A lock-protected, deterministic in-process Prometheus registry.
///
/// It intentionally has no `register` method.  Metric names and labels are
/// source-controlled constants, which prevents plugins or tenant input from
/// changing the production exposition contract.
pub struct MetricsRegistry {
    state: Mutex<MetricsState>,
}

impl Default for MetricsRegistry {
    fn default() -> Self {
        Self::new()
    }
}

impl MetricsRegistry {
    /// Creates a starting registry. Liveness begins true; startup and
    /// readiness remain false until the service completes admission.
    #[must_use]
    pub fn new() -> Self {
        Self { state: Mutex::new(MetricsState::default()) }
    }

    /// Records one request and its latency in the bounded operation families.
    pub fn record_request(
        &self,
        region: Region,
        operation: Operation,
        outcome: Outcome,
        duration: Duration,
    ) {
        let key = RequestKey { region, operation, outcome };
        let mut state = lock(&self.state);
        let total = state.request_totals.entry(key).or_default();
        *total = total.saturating_add(1);
        state.request_durations.entry(key).or_default().observe(duration);
    }

    /// Sets the current in-flight count for one bounded operation family.
    pub fn set_in_flight(&self, operation: Operation, count: u64) {
        lock(&self.state).in_flight.insert(operation, count);
    }

    /// Records one bounded admission rejection.
    pub fn record_admission_rejection(&self, operation: Operation, reason: AdmissionReason) {
        let mut state = lock(&self.state);
        saturating_increment(state.admission_rejections.entry((operation, reason)).or_default());
    }

    /// Sets one queue's current depth and non-zero configured capacity.
    pub fn set_queue(&self, queue: Queue, depth: u64, capacity: NonZeroU64) {
        lock(&self.state).queues.insert(queue, (depth, capacity.get()));
    }

    /// Records one WAL fsync result and latency.
    pub fn record_wal_fsync(&self, outcome: TaskOutcome, duration: Duration) {
        let mut state = lock(&self.state);
        let (total, histogram) = state.wal_fsync.entry(outcome).or_default();
        saturating_increment(total);
        histogram.observe(duration);
    }

    /// Records one commit result and end-to-end durability latency.
    pub fn record_commit(&self, result: CommitResult, duration: Duration) {
        let mut state = lock(&self.state);
        let (total, histogram) = state.commits.entry(result).or_default();
        saturating_increment(total);
        histogram.observe(duration);
    }

    /// Sets the current replication lag for a bounded Region.
    pub fn set_replication_lag(&self, region: Region, lag: Duration) {
        lock(&self.state).replication_lag_seconds.insert(region, lag.as_secs_f64());
    }

    /// Sets queued replication bytes for one bounded Region.
    pub fn set_replication_backlog_bytes(&self, region: Region, bytes: u64) {
        lock(&self.state).replication_backlog_bytes.insert(region, bytes);
    }

    /// Sets the verified frontier health for one bounded Region.
    pub fn set_replication_frontier_health(&self, region: Region, status: HealthStatus) {
        lock(&self.state).replication_frontier_health.insert(region, status);
    }

    /// Sets storage usage for a bounded storage tier.
    pub fn set_storage_bytes(&self, tier: StorageTier, bytes: u64) {
        lock(&self.state).storage_bytes.insert(tier, bytes);
    }

    /// Sets the configured usable capacity for a bounded storage tier.
    ///
    /// Callers omit this observation when a tier has no finite enforced
    /// capacity. Reporting an invented maximum would make utilization and
    /// headroom alerts unsafe.
    pub fn set_storage_capacity_bytes(&self, tier: StorageTier, bytes: u64) {
        lock(&self.state).storage_capacity_bytes.insert(tier, bytes);
    }

    /// Records one storage error in a bounded tier and operation class.
    pub fn record_storage_error(&self, tier: StorageTier, operation: StorageOperation) {
        let mut state = lock(&self.state);
        saturating_increment(state.storage_errors.entry((tier, operation)).or_default());
    }

    /// Sets compaction backlog bytes for one bounded storage tier.
    pub fn set_compaction_backlog_bytes(&self, tier: StorageTier, bytes: u64) {
        lock(&self.state).compaction_backlog_bytes.insert(tier, bytes);
    }

    /// Records one compaction result and the bytes it processed.
    pub fn record_compaction(&self, outcome: TaskOutcome, input_bytes: u64, output_bytes: u64) {
        let mut state = lock(&self.state);
        saturating_increment(state.compaction_runs.entry(outcome).or_default());
        state.compaction_input_bytes = state.compaction_input_bytes.saturating_add(input_bytes);
        state.compaction_output_bytes = state.compaction_output_bytes.saturating_add(output_bytes);
    }

    /// Sets the current age of the last verified successful backup.
    pub fn set_backup_last_success_age(&self, mode: BackupMode, age: Duration) {
        lock(&self.state).backup_last_success_age_seconds.insert(mode, age.as_secs_f64());
    }

    /// Records one backup result.
    pub fn record_backup(&self, mode: BackupMode, outcome: TaskOutcome) {
        let mut state = lock(&self.state);
        saturating_increment(state.backup_runs.entry((mode, outcome)).or_default());
    }

    /// Advances the authoritative usage-export frontier.
    ///
    /// A lower frontier is refused and leaves the prior observation intact.
    /// The return value reports whether the observation was accepted.
    pub fn advance_usage_export_frontier(&self, frontier: u64) -> bool {
        let mut state = lock(&self.state);
        if state.usage_export_frontier.is_some_and(|current| frontier < current) {
            return false;
        }
        state.usage_export_frontier = Some(frontier);
        true
    }

    /// Records one failed authoritative usage export.
    pub fn record_usage_export_failure(&self) {
        let mut state = lock(&self.state);
        state.usage_export_failures = state.usage_export_failures.saturating_add(1);
    }

    /// Sets the active member count for a bounded Region.
    pub fn set_active_members(&self, region: Region, members: u64) {
        lock(&self.state).active_members.insert(region, members);
    }

    /// Records one component's health gauge.
    pub fn set_component_health(&self, component: Component, status: HealthStatus) {
        let mut state = lock(&self.state);
        state.component_health.insert(component, status);
        state.health_ready = required_health_is_ready(&state.component_health);
    }

    /// Copies a health report into the metric registry and derives readiness.
    pub fn observe_health(&self, report: &HealthReport) {
        let mut state = lock(&self.state);
        state.component_health.clear();
        for check in report.checks() {
            state.component_health.insert(check.component(), check.status());
        }
        state.health_ready = report.is_ready();
    }

    /// Advances the bounded service lifecycle.
    ///
    /// Repeating the current state is idempotent. Backwards transitions and
    /// every transition after [`ServiceState::Stopped`] fail closed. Reaching
    /// [`ServiceState::Serving`] records the monotonic startup fact.
    ///
    /// # Errors
    ///
    /// Returns [`ServiceTransitionError`] for a backwards transition or any
    /// transition away from [`ServiceState::Stopped`].
    pub fn transition_service(
        &self,
        service_state: ServiceState,
    ) -> Result<(), ServiceTransitionError> {
        let mut state = lock(&self.state);
        if !state.service_state.permits_transition_to(service_state) {
            return Err(ServiceTransitionError { from: state.service_state, to: service_state });
        }
        state.service_state = service_state;
        state.started |= matches!(service_state, ServiceState::Serving);
        Ok(())
    }

    /// Takes one immutable snapshot for private operator adapters.
    #[must_use]
    #[allow(
        clippy::too_many_lines,
        reason = "one locked pass copies every closed metric family into one atomic snapshot"
    )]
    pub fn snapshot(&self) -> MetricsSnapshot {
        let state = lock(&self.state);
        let requests = state
            .request_totals
            .iter()
            .map(|(key, total)| {
                let histogram = state.request_durations.get(key).copied().unwrap_or_default();
                RequestSeries {
                    region: key.region,
                    operation: key.operation,
                    outcome: key.outcome,
                    total: *total,
                    duration: HistogramSnapshot::from_histogram(histogram),
                }
            })
            .collect();
        let in_flight =
            state.in_flight.iter().map(|(operation, count)| (*operation, *count)).collect();
        let admission_rejections = state
            .admission_rejections
            .iter()
            .map(|((operation, reason), total)| (*operation, *reason, *total))
            .collect();
        let queues = state
            .queues
            .iter()
            .map(|(queue, (depth, capacity))| (*queue, *depth, *capacity))
            .collect();
        let wal_fsync = state
            .wal_fsync
            .iter()
            .map(|(outcome, (total, histogram))| {
                (*outcome, *total, HistogramSnapshot::from_histogram(*histogram))
            })
            .collect();
        let commits = state
            .commits
            .iter()
            .map(|(result, (total, histogram))| {
                (*result, *total, HistogramSnapshot::from_histogram(*histogram))
            })
            .collect();
        let replication_lag = state
            .replication_lag_seconds
            .iter()
            .map(|(region, seconds)| ReplicationLag { region: *region, seconds: *seconds })
            .collect();
        let replication_backlog = state
            .replication_backlog_bytes
            .iter()
            .map(|(region, bytes)| (*region, *bytes))
            .collect();
        let replication_frontier_health = state
            .replication_frontier_health
            .iter()
            .map(|(region, status)| (*region, *status))
            .collect();
        let storage = state
            .storage_bytes
            .iter()
            .map(|(tier, bytes)| StorageUsage { tier: *tier, bytes: *bytes })
            .collect();
        let storage_capacity = state
            .storage_capacity_bytes
            .iter()
            .map(|(tier, bytes)| StorageCapacity { tier: *tier, bytes: *bytes })
            .collect();
        let storage_errors = state
            .storage_errors
            .iter()
            .map(|((tier, operation), total)| (*tier, *operation, *total))
            .collect();
        let active_members = state
            .active_members
            .iter()
            .map(|(region, members)| ActiveMembers { region: *region, members: *members })
            .collect();
        let component_health = COMPONENTS
            .iter()
            .map(|component| ComponentHealth {
                component: *component,
                status: state
                    .component_health
                    .get(component)
                    .copied()
                    .unwrap_or(HealthStatus::Unknown),
            })
            .collect();
        let compaction_backlog = state
            .compaction_backlog_bytes
            .iter()
            .map(|(tier, bytes)| StorageUsage { tier: *tier, bytes: *bytes })
            .collect();
        let compaction_runs =
            state.compaction_runs.iter().map(|(outcome, total)| (*outcome, *total)).collect();
        let backup_last_success_age = state
            .backup_last_success_age_seconds
            .iter()
            .map(|(mode, seconds)| (*mode, *seconds))
            .collect();
        let backup_runs = state
            .backup_runs
            .iter()
            .map(|((mode, outcome), total)| (*mode, *outcome, *total))
            .collect();
        MetricsSnapshot {
            service_state: state.service_state,
            started: state.started,
            ready: state.service_state.permits_readiness() && state.health_ready,
            requests,
            in_flight,
            admission_rejections,
            queues,
            wal_fsync,
            commits,
            replication_lag,
            replication_backlog,
            replication_frontier_health,
            storage,
            storage_capacity,
            storage_errors,
            active_members,
            component_health,
            compaction_backlog,
            compaction_runs,
            compaction_input_bytes: state.compaction_input_bytes,
            compaction_output_bytes: state.compaction_output_bytes,
            backup_last_success_age,
            backup_runs,
            usage_export_frontier: state.usage_export_frontier,
            usage_export_failures: state.usage_export_failures,
        }
    }

    /// Renders the complete stable Prometheus text exposition.
    #[must_use]
    pub fn render_prometheus(&self) -> String {
        self.snapshot().render_prometheus()
    }
}

fn write_process_metrics(output: &mut String, live: bool, started: bool, ready: bool) {
    write_help_type(
        output,
        "terrabase_liveness",
        "Process liveness (1 while the process is running).",
        "gauge",
    );
    writeln!(output, "terrabase_liveness {}", u8::from(live)).expect("writing String cannot fail");
    write_help_type(
        output,
        "terrabase_started",
        "Startup completion (1 after the service first reached serving).",
        "gauge",
    );
    writeln!(output, "terrabase_started {}", u8::from(started))
        .expect("writing String cannot fail");
    write_help_type(
        output,
        "terrabase_ready",
        "Readiness after required dependency and admission checks (1 or 0).",
        "gauge",
    );
    writeln!(output, "terrabase_ready {}", u8::from(ready)).expect("writing String cannot fail");
}

fn write_request_metrics(output: &mut String, snapshot: &MetricsSnapshot) {
    write_help_type(
        output,
        "terrabase_requests_total",
        "Completed TerraBase requests by bounded Region, operation, and outcome.",
        "counter",
    );
    for series in &snapshot.requests {
        writeln!(
            output,
            "terrabase_requests_total{{{}}} {}",
            request_labels(series.key()),
            series.total
        )
        .expect("writing String cannot fail");
    }
    write_help_type(
        output,
        "terrabase_request_duration_seconds",
        "TerraBase request duration in seconds by bounded Region, operation, and outcome.",
        "histogram",
    );
    for series in &snapshot.requests {
        write_histogram(output, series);
    }
}

fn write_histogram(output: &mut String, series: &RequestSeries) {
    let labels = request_labels(series.key());
    write_duration_histogram(
        output,
        "terrabase_request_duration_seconds",
        &labels,
        series.duration,
    );
}

fn write_duration_histogram(
    output: &mut String,
    name: &str,
    labels: &str,
    histogram: HistogramSnapshot,
) {
    for (index, upper_bound) in REQUEST_LATENCY_BUCKETS.iter().enumerate() {
        writeln!(
            output,
            "{name}_bucket{{{labels},le=\"{}\"}} {}",
            format_bound(*upper_bound),
            histogram.cumulative_buckets[index]
        )
        .expect("writing String cannot fail");
    }
    writeln!(output, "{name}_bucket{{{labels},le=\"+Inf\"}} {}", histogram.count)
        .expect("writing String cannot fail");
    writeln!(output, "{name}_sum{{{labels}}} {}", format_float(histogram.sum_seconds))
        .expect("writing String cannot fail");
    writeln!(output, "{name}_count{{{labels}}} {}", histogram.count)
        .expect("writing String cannot fail");
}

fn write_admission_metrics(output: &mut String, snapshot: &MetricsSnapshot) {
    write_help_type(
        output,
        "terrabase_in_flight_operations",
        "Current in-flight operations by bounded operation family.",
        "gauge",
    );
    for (operation, count) in &snapshot.in_flight {
        writeln!(
            output,
            "terrabase_in_flight_operations{{operation=\"{}\"}} {count}",
            operation.as_str()
        )
        .expect("writing String cannot fail");
    }
    write_help_type(
        output,
        "terrabase_admission_rejections_total",
        "Admission rejections by bounded operation and reason.",
        "counter",
    );
    for (operation, reason, total) in &snapshot.admission_rejections {
        writeln!(
            output,
            "terrabase_admission_rejections_total{{operation=\"{}\",reason=\"{}\"}} {total}",
            operation.as_str(),
            reason.as_str()
        )
        .expect("writing String cannot fail");
    }
    write_help_type(
        output,
        "terrabase_queue_depth",
        "Current work queue depth by bounded queue.",
        "gauge",
    );
    for (queue, depth, _) in &snapshot.queues {
        writeln!(output, "terrabase_queue_depth{{queue=\"{}\"}} {depth}", queue.as_str())
            .expect("writing String cannot fail");
    }
    write_help_type(
        output,
        "terrabase_queue_capacity",
        "Configured non-zero work queue capacity by bounded queue.",
        "gauge",
    );
    for (queue, _, capacity) in &snapshot.queues {
        writeln!(output, "terrabase_queue_capacity{{queue=\"{}\"}} {capacity}", queue.as_str())
            .expect("writing String cannot fail");
    }
}

fn write_durability_metrics(output: &mut String, snapshot: &MetricsSnapshot) {
    write_help_type(
        output,
        "terrabase_wal_fsync_total",
        "WAL fsync attempts by bounded outcome.",
        "counter",
    );
    for (outcome, total, _) in &snapshot.wal_fsync {
        writeln!(output, "terrabase_wal_fsync_total{{outcome=\"{}\"}} {total}", outcome.as_str())
            .expect("writing String cannot fail");
    }
    write_help_type(
        output,
        "terrabase_wal_fsync_duration_seconds",
        "WAL fsync latency in seconds by bounded outcome.",
        "histogram",
    );
    for (outcome, _, histogram) in &snapshot.wal_fsync {
        write_duration_histogram(
            output,
            "terrabase_wal_fsync_duration_seconds",
            &format!("outcome=\"{}\"", outcome.as_str()),
            *histogram,
        );
    }
    write_help_type(
        output,
        "terrabase_commits_total",
        "Commit attempts by bounded durability result.",
        "counter",
    );
    for (result, total, _) in &snapshot.commits {
        writeln!(output, "terrabase_commits_total{{outcome=\"{}\"}} {total}", result.as_str())
            .expect("writing String cannot fail");
    }
    write_help_type(
        output,
        "terrabase_commit_duration_seconds",
        "End-to-end commit durability latency in seconds by bounded result.",
        "histogram",
    );
    for (result, _, histogram) in &snapshot.commits {
        write_duration_histogram(
            output,
            "terrabase_commit_duration_seconds",
            &format!("outcome=\"{}\"", result.as_str()),
            *histogram,
        );
    }
}

fn write_resource_metrics(output: &mut String, snapshot: &MetricsSnapshot) {
    write_replication_metrics(output, snapshot);
    write_storage_metrics(output, snapshot);
    write_membership_metrics(output, snapshot);
}

fn write_replication_metrics(output: &mut String, snapshot: &MetricsSnapshot) {
    write_help_type(
        output,
        "terrabase_replication_lag_seconds",
        "Observed replication lag in seconds by bounded Region.",
        "gauge",
    );
    for observation in &snapshot.replication_lag {
        writeln!(
            output,
            "terrabase_replication_lag_seconds{{region=\"{}\"}} {}",
            observation.region.as_str(),
            format_float(observation.seconds)
        )
        .expect("writing String cannot fail");
    }
    write_help_type(
        output,
        "terrabase_replication_backlog_bytes",
        "Queued replication bytes by bounded Region.",
        "gauge",
    );
    for (region, bytes) in &snapshot.replication_backlog {
        writeln!(
            output,
            "terrabase_replication_backlog_bytes{{region=\"{}\"}} {bytes}",
            region.as_str()
        )
        .expect("writing String cannot fail");
    }
    write_help_type(
        output,
        "terrabase_replication_frontier_health",
        "Verified replication frontier health by bounded Region.",
        "gauge",
    );
    for (region, status) in &snapshot.replication_frontier_health {
        writeln!(
            output,
            "terrabase_replication_frontier_health{{region=\"{}\"}} {}",
            region.as_str(),
            format_float(status.gauge_value())
        )
        .expect("writing String cannot fail");
    }
}

fn write_storage_metrics(output: &mut String, snapshot: &MetricsSnapshot) {
    write_help_type(
        output,
        "terrabase_storage_bytes",
        "Observed storage bytes by bounded storage tier.",
        "gauge",
    );
    for observation in &snapshot.storage {
        writeln!(
            output,
            "terrabase_storage_bytes{{tier=\"{}\"}} {}",
            observation.tier.as_str(),
            observation.bytes
        )
        .expect("writing String cannot fail");
    }
    write_help_type(
        output,
        "terrabase_storage_capacity_bytes",
        "Configured usable storage capacity in bytes by bounded storage tier.",
        "gauge",
    );
    for observation in &snapshot.storage_capacity {
        writeln!(
            output,
            "terrabase_storage_capacity_bytes{{tier=\"{}\"}} {}",
            observation.tier.as_str(),
            observation.bytes
        )
        .expect("writing String cannot fail");
    }
    write_help_type(
        output,
        "terrabase_storage_errors_total",
        "Storage errors by bounded tier and operation.",
        "counter",
    );
    for (tier, operation, total) in &snapshot.storage_errors {
        writeln!(
            output,
            "terrabase_storage_errors_total{{tier=\"{}\",operation=\"{}\"}} {total}",
            tier.as_str(),
            operation.as_str()
        )
        .expect("writing String cannot fail");
    }
}

fn write_membership_metrics(output: &mut String, snapshot: &MetricsSnapshot) {
    write_help_type(
        output,
        "terrabase_active_members",
        "Active TerraBase members by bounded Region.",
        "gauge",
    );
    for observation in &snapshot.active_members {
        writeln!(
            output,
            "terrabase_active_members{{region=\"{}\"}} {}",
            observation.region.as_str(),
            observation.members
        )
        .expect("writing String cannot fail");
    }
}

fn write_background_metrics(output: &mut String, snapshot: &MetricsSnapshot) {
    write_help_type(
        output,
        "terrabase_compaction_backlog_bytes",
        "Pending compaction bytes by bounded storage tier.",
        "gauge",
    );
    for observation in &snapshot.compaction_backlog {
        writeln!(
            output,
            "terrabase_compaction_backlog_bytes{{tier=\"{}\"}} {}",
            observation.tier.as_str(),
            observation.bytes
        )
        .expect("writing String cannot fail");
    }
    write_help_type(
        output,
        "terrabase_compaction_runs_total",
        "Compaction runs by bounded outcome.",
        "counter",
    );
    for (outcome, total) in &snapshot.compaction_runs {
        writeln!(
            output,
            "terrabase_compaction_runs_total{{outcome=\"{}\"}} {total}",
            outcome.as_str()
        )
        .expect("writing String cannot fail");
    }
    write_help_type(
        output,
        "terrabase_compaction_input_bytes_total",
        "Bytes read by completed or failed compaction runs.",
        "counter",
    );
    writeln!(output, "terrabase_compaction_input_bytes_total {}", snapshot.compaction_input_bytes)
        .expect("writing String cannot fail");
    write_help_type(
        output,
        "terrabase_compaction_output_bytes_total",
        "Bytes written by completed or failed compaction runs.",
        "counter",
    );
    writeln!(
        output,
        "terrabase_compaction_output_bytes_total {}",
        snapshot.compaction_output_bytes
    )
    .expect("writing String cannot fail");
    write_help_type(
        output,
        "terrabase_backup_last_success_age_seconds",
        "Age in seconds of the last verified successful backup by bounded mode.",
        "gauge",
    );
    for (mode, seconds) in &snapshot.backup_last_success_age {
        writeln!(
            output,
            "terrabase_backup_last_success_age_seconds{{mode=\"{}\"}} {}",
            mode.as_str(),
            format_float(*seconds)
        )
        .expect("writing String cannot fail");
    }
    write_help_type(
        output,
        "terrabase_backup_runs_total",
        "Backup runs by bounded mode and outcome.",
        "counter",
    );
    for (mode, outcome, total) in &snapshot.backup_runs {
        writeln!(
            output,
            "terrabase_backup_runs_total{{mode=\"{}\",outcome=\"{}\"}} {total}",
            mode.as_str(),
            outcome.as_str()
        )
        .expect("writing String cannot fail");
    }
    write_help_type(
        output,
        "terrabase_usage_export_frontier",
        "Last exported authoritative usage sequence frontier.",
        "gauge",
    );
    if let Some(frontier) = snapshot.usage_export_frontier {
        writeln!(output, "terrabase_usage_export_frontier {frontier}")
            .expect("writing String cannot fail");
    }
    write_help_type(
        output,
        "terrabase_usage_export_failures_total",
        "Failed authoritative usage export attempts.",
        "counter",
    );
    writeln!(output, "terrabase_usage_export_failures_total {}", snapshot.usage_export_failures)
        .expect("writing String cannot fail");
}

fn write_health_metrics(output: &mut String, snapshot: &MetricsSnapshot) {
    write_help_type(
        output,
        "terrabase_component_health",
        "Component health gauge: healthy 1, degraded 0.5, unhealthy 0, unknown -1.",
        "gauge",
    );
    for observation in &snapshot.component_health {
        writeln!(
            output,
            "terrabase_component_health{{component=\"{}\"}} {}",
            observation.component.as_str(),
            format_float(observation.status.gauge_value())
        )
        .expect("writing String cannot fail");
    }
}

fn request_labels(key: RequestKey) -> String {
    format!(
        "region=\"{}\",operation=\"{}\",outcome=\"{}\"",
        key.region.as_str(),
        key.operation.as_str(),
        key.outcome.as_str()
    )
}

fn write_help_type(output: &mut String, name: &str, help: &str, metric_type: &str) {
    writeln!(output, "# HELP {name} {help}").expect("writing String cannot fail");
    writeln!(output, "# TYPE {name} {metric_type}").expect("writing String cannot fail");
}

fn format_bound(value: f64) -> String {
    format_float(value)
}

fn format_float(value: f64) -> String {
    if value == 0.0 {
        "0".to_owned()
    } else {
        format!("{value:.9}").trim_end_matches('0').trim_end_matches('.').to_owned()
    }
}

fn lock<T>(mutex: &Mutex<T>) -> MutexGuard<'_, T> {
    mutex.lock().unwrap_or_else(std::sync::PoisonError::into_inner)
}

fn saturating_increment(value: &mut u64) {
    *value = value.saturating_add(1);
}

/// Redacts secrets from diagnostic text before it is handed to logs or an
/// operator-facing health adapter.
///
/// The observability contract never exports free-form diagnostic text in
/// Prometheus labels.  This helper exists for adapters that need to display a
/// safe reason alongside a probe result.  It handles bearer credentials and
/// a closed set of exact or namespaced key/value names case-insensitively,
/// while retaining the key and surrounding punctuation for useful diagnostics.
/// Input beyond [`MAX_DIAGNOSTIC_BYTES`] is not inspected or returned.
#[must_use]
pub fn redact_diagnostic(input: &str) -> String {
    let input = bounded_diagnostic(input);
    let mut output = String::with_capacity(input.len());
    let mut scan = 0;
    while scan < input.len() {
        let Some(start) = next_key_start(input, scan) else {
            output.push_str(&input[scan..]);
            break;
        };
        output.push_str(&input[scan..start]);

        let mut key_end = key_end(input, start);
        // A colon is both a namespace separator and a header/assignment
        // delimiter.  Prefer the delimiter interpretation when the portion
        // before it is itself a sensitive key, so `Authorization:Bearer`
        // cannot hide behind one oversized candidate.
        if !input.as_bytes().get(key_end).is_some_and(|byte| *byte == b'=' || *byte == b':') {
            if let Some(relative) = input[start..key_end].rfind(':') {
                let prefix_end = start + relative;
                if is_secret_key(&input[start..prefix_end]) {
                    key_end = prefix_end;
                }
            }
        }
        let key = &input[start..key_end];
        if is_secret_key(key) {
            if let Some((value_start, value_end)) = secret_value_range(input, key_end, key) {
                output.push_str(&input[start..value_start]);
                output.push_str("[REDACTED]");
                scan = value_end;
                continue;
            }
        }
        output.push_str(&input[start..key_end]);
        scan = key_end;
    }
    output
}

const SECRET_KEY_ALIASES: &[&str] = &[
    "aws_secret_access_key",
    "aws-secret-access-key",
    "proxy-authorization",
    "authorization",
    "client_secret",
    "client-secret",
    "secret_access_key",
    "secret-access-key",
    "access_token",
    "access-token",
    "access_key",
    "access-key",
    "private_key",
    "private-key",
    "x-api-key",
    "api_key",
    "api-key",
    "apikey",
    "set-cookie",
    "credentials",
    "credential",
    "password",
    "passwd",
    "bearer",
    "secret",
    "token",
    "cookie",
];

fn bounded_diagnostic(input: &str) -> &str {
    if input.len() <= MAX_DIAGNOSTIC_BYTES {
        return input;
    }
    let mut end = MAX_DIAGNOSTIC_BYTES;
    while !input.is_char_boundary(end) {
        end -= 1;
    }
    &input[..end]
}

fn next_key_start(input: &str, from: usize) -> Option<usize> {
    let mut previous_is_path = input[..from].chars().next_back().is_some_and(is_key_path_char);
    for (offset, character) in input[from..].char_indices() {
        let is_start = is_key_atom_char(character) && !previous_is_path;
        previous_is_path = is_key_path_char(character);
        if is_start {
            return Some(from + offset);
        }
    }
    None
}

fn key_end(input: &str, start: usize) -> usize {
    let mut cursor = start;
    let mut characters = input[start..].char_indices().peekable();
    while let Some((offset, character)) = characters.next() {
        let include = if character == ':' {
            characters
                .peek()
                .is_some_and(|(_, next)| is_key_atom_char(*next) || is_unicode_trick(*next))
        } else {
            is_key_atom_char(character) || is_unicode_trick(character)
        };
        if !include {
            break;
        }
        cursor = start + offset + character.len_utf8();
    }
    cursor
}

fn is_secret_key(key: &str) -> bool {
    SECRET_KEY_ALIASES.iter().any(|alias| has_secret_suffix(key, alias))
}

fn has_secret_suffix(key: &str, alias: &str) -> bool {
    let key = trim_unicode_tricks(key);
    if ascii_eq_ignore_case(key, alias) {
        return true;
    }
    let Some(offset) = key.len().checked_sub(alias.len()) else { return false };
    offset > 0
        && key[..offset].chars().next_back().is_some_and(is_namespace_separator)
        && ascii_eq_ignore_case(&key[offset..], alias)
}

fn trim_unicode_tricks(mut key: &str) -> &str {
    while let Some((offset, character)) = key.char_indices().next_back() {
        if !is_unicode_trick(character) {
            break;
        }
        key = &key[..offset];
    }
    key
}

fn ascii_eq_ignore_case(value: &str, expected: &str) -> bool {
    value.len() == expected.len()
        && value
            .bytes()
            .zip(expected.bytes())
            .all(|(value, expected)| value.eq_ignore_ascii_case(&expected))
}

fn secret_value_range(
    input: &str,
    key_end: usize,
    key: &str,
) -> Option<(usize, usize)> {
    let mut cursor = key_end;
    skip_whitespace(input, &mut cursor);
    let bare_bearer = has_secret_suffix(key, "bearer")
        && ascii_eq_ignore_case(trim_unicode_tricks(key), "bearer");
    if input.as_bytes().get(cursor).is_some_and(|byte| *byte == b'=' || *byte == b':') {
        cursor += 1;
        skip_whitespace(input, &mut cursor);
    } else if !bare_bearer {
        return None;
    }

    let value_start = cursor;
    let quote = input[value_start..]
        .chars()
        .next()
        .filter(|character| *character == '"' || *character == '\'');
    if let Some(quote) = quote {
        let content_start = value_start + quote.len_utf8();
        let value_end = quoted_value_end(input, content_start, quote);
        return (value_end > content_start).then_some((content_start, value_end));
    }

    let mut content_start = value_start;
    if has_secret_suffix(key, "authorization") || has_secret_suffix(key, "proxy-authorization") {
        for scheme in ["bearer", "basic"] {
            if starts_with_ascii_ignore_case(&input[content_start..], scheme) {
                let after_scheme = content_start + scheme.len();
                if input[after_scheme..].chars().next().is_some_and(char::is_whitespace) {
                    content_start = after_scheme;
                    skip_whitespace(input, &mut content_start);
                    break;
                }
            }
        }
    }
    let value_end = unquoted_value_end(input, content_start);
    (value_end > content_start).then_some((value_start, value_end))
}

fn skip_whitespace(input: &str, cursor: &mut usize) {
    while input[*cursor..].chars().next().is_some_and(char::is_whitespace) {
        *cursor += input[*cursor..].chars().next().expect("checked above").len_utf8();
    }
}

fn quoted_value_end(input: &str, start: usize, quote: char) -> usize {
    let mut escaped = false;
    for (offset, character) in input[start..].char_indices() {
        if escaped {
            escaped = false;
        } else if character == '\\' {
            escaped = true;
        } else if character == quote {
            return start + offset;
        }
    }
    input.len()
}

fn unquoted_value_end(input: &str, start: usize) -> usize {
    for (offset, character) in input[start..].char_indices() {
        if character.is_whitespace() || matches!(character, ',' | ';' | '&' | '#' | '}' | ']') {
            return start + offset;
        }
    }
    input.len()
}

fn starts_with_ascii_ignore_case(value: &str, expected: &str) -> bool {
    value.len() >= expected.len()
        && value.as_bytes()[..expected.len()]
            .iter()
            .zip(expected.bytes())
            .all(|(value, expected)| value.eq_ignore_ascii_case(&expected))
}

fn is_key_atom_char(character: char) -> bool {
    character.is_alphanumeric() || matches!(character, '_' | '-' | '.')
}

fn is_key_path_char(character: char) -> bool {
    is_key_atom_char(character) || character == ':' || is_unicode_trick(character)
}

const fn is_namespace_separator(character: char) -> bool {
    matches!(character, '_' | '-' | '.' | ':') || is_unicode_trick(character)
}

const fn is_unicode_trick(character: char) -> bool {
    matches!(character, '\u{200b}'..='\u{200d}' | '\u{2060}' | '\u{feff}' | '\u{fe00}'..='\u{fe0f}')
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn unknown_region_does_not_normalize_or_leak_provider_names() {
        assert_eq!(Region::from_code("GRU"), Region::Gru);
        assert_eq!(Region::from_code("gru"), Region::Unknown);
        assert_eq!(Region::from_code("sa-saopaulo-1"), Region::Unknown);
        assert_eq!(Region::from_code("SJC"), Region::Unknown);
    }

    #[test]
    fn health_and_readiness_are_separate() {
        let registry = HealthRegistry::new();
        registry.set(Component::Storage, HealthStatus::Unhealthy);
        registry.set(Component::Backup, HealthStatus::Healthy);
        let report = registry.snapshot();
        assert_eq!(report.status(), HealthStatus::Unhealthy);
        assert!(!report.is_ready());
        assert_eq!(report.health_status_code(), 503);
        assert_eq!(report.readiness_status_code(), 503);
        assert!(registry.liveness());
    }

    #[test]
    fn unknown_health_fails_closed_but_liveness_stays_up() {
        let registry = HealthRegistry::new();
        let report = registry.snapshot();
        assert_eq!(report.status(), HealthStatus::Unknown);
        assert_eq!(report.health_status_code(), 503);
        assert_eq!(report.readiness_status_code(), 503);
        assert!(registry.liveness());
    }

    #[test]
    fn optional_backup_does_not_make_healthy_core_unready() {
        let registry = HealthRegistry::new();
        for component in COMPONENTS {
            registry.set(
                component,
                if component == Component::Backup {
                    HealthStatus::Degraded
                } else {
                    HealthStatus::Healthy
                },
            );
        }
        let report = registry.snapshot();
        assert_eq!(report.status(), HealthStatus::Degraded);
        assert!(report.is_ready());
        assert_eq!(report.health_status_code(), 200);
        assert_eq!(report.readiness_status_code(), 200);
    }

    #[test]
    fn admitted_member_stays_ready_when_only_control_plane_is_lost() {
        let health = HealthRegistry::new();
        for component in REQUIRED_COMPONENTS {
            health.set(component, HealthStatus::Healthy);
        }
        health.set(Component::ControlPlane, HealthStatus::Unhealthy);
        let report = health.snapshot();
        assert_eq!(report.status(), HealthStatus::Unhealthy);
        assert!(report.is_ready());
        assert_eq!(report.health_status_code(), 503);
        assert_eq!(report.readiness_status_code(), 200);

        let metrics = MetricsRegistry::new();
        metrics.observe_health(&report);
        metrics
            .transition_service(ServiceState::Serving)
            .expect("admission completed before the control-plane loss");
        let snapshot = metrics.snapshot();
        assert!(snapshot.is_ready());
        assert_eq!(
            snapshot
                .component_health()
                .iter()
                .find(|health| health.component() == Component::ControlPlane)
                .expect("control-plane slot is always present")
                .status(),
            HealthStatus::Unhealthy
        );
    }

    #[test]
    fn histogram_buckets_are_cumulative_and_have_inf_bucket() {
        let metrics = MetricsRegistry::new();
        metrics.record_request(
            Region::Gru,
            Operation::Read,
            Outcome::Success,
            Duration::from_millis(7),
        );
        metrics.record_request(
            Region::Gru,
            Operation::Read,
            Outcome::Success,
            Duration::from_millis(30),
        );
        let exposition = metrics.render_prometheus();
        assert!(exposition.contains("terrabase_request_duration_seconds_bucket{region=\"GRU\",operation=\"read\",outcome=\"success\",le=\"0.01\"} 1"));
        assert!(exposition.contains("terrabase_request_duration_seconds_bucket{region=\"GRU\",operation=\"read\",outcome=\"success\",le=\"0.05\"} 2"));
        assert!(exposition.contains("terrabase_request_duration_seconds_bucket{region=\"GRU\",operation=\"read\",outcome=\"success\",le=\"+Inf\"} 2"));
        assert!(exposition.contains("terrabase_request_duration_seconds_count{region=\"GRU\",operation=\"read\",outcome=\"success\"} 2"));
    }

    #[test]
    fn exposition_is_redacted_by_construction_and_has_only_known_labels() {
        let metrics = MetricsRegistry::new();
        metrics.record_request(
            Region::Unknown,
            Operation::Other,
            Outcome::Rejected,
            Duration::ZERO,
        );
        metrics.set_storage_bytes(StorageTier::Tier2, 42);
        let exposition = metrics.render_prometheus();
        assert!(exposition.contains("region=\"unknown\""));
        assert!(exposition.contains("operation=\"other\""));
        assert!(!exposition.contains("tenant"));
        assert!(!exposition.contains("document"));
        assert!(!exposition.contains("authorization"));
    }

    #[test]
    fn readiness_is_false_until_observed_required_components_are_healthy() {
        let health = HealthRegistry::new();
        let metrics = MetricsRegistry::new();
        metrics.observe_health(&health.snapshot());
        assert!(metrics.render_prometheus().contains("terrabase_ready 0"));
        for component in COMPONENTS {
            health.set(component, HealthStatus::Healthy);
        }
        metrics.observe_health(&health.snapshot());
        metrics.transition_service(ServiceState::Serving).expect("starting may advance to serving");
        assert!(metrics.render_prometheus().contains("terrabase_ready 1"));
    }

    #[test]
    fn missing_or_conflicting_required_checks_fail_closed() {
        let missing = HealthReport { status: HealthStatus::Healthy, checks: Vec::new() };
        assert!(!missing.is_ready());

        let conflicting = HealthReport {
            status: HealthStatus::Unhealthy,
            checks: vec![
                HealthCheck { component: Component::Storage, status: HealthStatus::Healthy },
                HealthCheck { component: Component::Storage, status: HealthStatus::Unhealthy },
                HealthCheck { component: Component::Replication, status: HealthStatus::Healthy },
                HealthCheck { component: Component::Membership, status: HealthStatus::Healthy },
                HealthCheck { component: Component::ControlPlane, status: HealthStatus::Healthy },
            ],
        };
        assert!(!conflicting.is_ready());
    }

    #[test]
    fn lifecycle_gates_live_startup_and_readiness_without_backwards_moves() {
        let metrics = MetricsRegistry::new();
        for component in REQUIRED_COMPONENTS {
            metrics.set_component_health(component, HealthStatus::Healthy);
        }
        let starting = metrics.snapshot();
        assert!(starting.is_live());
        assert!(!starting.is_started());
        assert!(!starting.is_ready());

        metrics.transition_service(ServiceState::Serving).expect("starting may advance to serving");
        let serving = metrics.snapshot();
        assert!(serving.is_live());
        assert!(serving.is_started());
        assert!(serving.is_ready());

        metrics
            .transition_service(ServiceState::Draining)
            .expect("serving may advance to draining");
        let draining = metrics.snapshot();
        assert!(draining.is_live());
        assert!(draining.is_started());
        assert!(!draining.is_ready());

        metrics.transition_service(ServiceState::Stopped).expect("draining may advance to stopped");
        let stopped = metrics.snapshot();
        assert!(!stopped.is_live());
        assert!(stopped.is_started());
        assert!(!stopped.is_ready());
        assert_eq!(
            metrics.transition_service(ServiceState::Serving),
            Err(ServiceTransitionError { from: ServiceState::Stopped, to: ServiceState::Serving })
        );
    }

    #[test]
    fn immutable_snapshot_and_prometheus_rendering_share_one_fact_set() {
        let metrics = MetricsRegistry::new();
        metrics.record_request(
            Region::Fra,
            Operation::Query,
            Outcome::Timeout,
            Duration::from_millis(25),
        );
        let before = metrics.snapshot();
        metrics.record_request(
            Region::Fra,
            Operation::Query,
            Outcome::Timeout,
            Duration::from_millis(75),
        );
        let after = metrics.snapshot();
        assert_eq!(before.requests()[0].total(), 1);
        assert_eq!(before.requests()[0].duration().count(), 1);
        assert_eq!(after.requests()[0].total(), 2);
        assert_eq!(after.requests()[0].duration().count(), 2);
        assert!(before.render_prometheus().contains(
            "terrabase_requests_total{region=\"FRA\",operation=\"query\",outcome=\"timeout\"} 1"
        ));
        assert!(after.render_prometheus().contains(
            "terrabase_requests_total{region=\"FRA\",operation=\"query\",outcome=\"timeout\"} 2"
        ));
    }

    #[test]
    fn closed_label_product_bounds_a_fully_populated_exposition() {
        let metrics = MetricsRegistry::new();
        for region in REGIONS {
            for operation in OPERATIONS {
                for outcome in OUTCOMES {
                    metrics.record_request(region, operation, outcome, Duration::from_secs(20));
                }
            }
            metrics.set_replication_lag(region, Duration::MAX);
            metrics.set_replication_backlog_bytes(region, u64::MAX);
            metrics.set_replication_frontier_health(region, HealthStatus::Unknown);
            metrics.set_active_members(region, u64::MAX);
        }
        for operation in OPERATIONS {
            metrics.set_in_flight(operation, u64::MAX);
            for reason in ADMISSION_REASONS {
                metrics.record_admission_rejection(operation, reason);
            }
        }
        for queue in QUEUES {
            metrics.set_queue(queue, u64::MAX, NonZeroU64::MAX);
        }
        for outcome in TASK_OUTCOMES {
            metrics.record_wal_fsync(outcome, Duration::MAX);
            metrics.record_compaction(outcome, u64::MAX, u64::MAX);
        }
        for result in COMMIT_RESULTS {
            metrics.record_commit(result, Duration::MAX);
        }
        for tier in STORAGE_TIERS {
            metrics.set_storage_bytes(tier, u64::MAX);
            metrics.set_storage_capacity_bytes(tier, u64::MAX);
            metrics.set_compaction_backlog_bytes(tier, u64::MAX);
            for operation in STORAGE_OPERATIONS {
                metrics.record_storage_error(tier, operation);
            }
        }
        for mode in BACKUP_MODES {
            metrics.set_backup_last_success_age(mode, Duration::MAX);
            for outcome in TASK_OUTCOMES {
                metrics.record_backup(mode, outcome);
            }
        }
        for component in COMPONENTS {
            metrics.set_component_health(component, HealthStatus::Unknown);
        }
        assert!(metrics.advance_usage_export_frontier(u64::MAX));
        metrics.record_usage_export_failure();
        let snapshot = metrics.snapshot();
        assert_eq!(snapshot.requests().len(), MAX_REQUEST_SERIES);
        assert_eq!(snapshot.replication_lag().len(), MAX_REGION_LABEL_VALUES);
        assert_eq!(snapshot.storage().len(), MAX_STORAGE_TIER_LABEL_VALUES);
        assert_eq!(snapshot.storage_capacity().len(), MAX_STORAGE_TIER_LABEL_VALUES);
        assert_eq!(snapshot.active_members().len(), MAX_REGION_LABEL_VALUES);
        assert_eq!(snapshot.component_health().len(), MAX_COMPONENT_LABEL_VALUES);
        let exposition = snapshot.render_prometheus();
        assert_eq!(
            snapshot.try_render_prometheus().expect("closed registry fits the body limit"),
            exposition
        );
        assert!(exposition.len() <= MAX_PROMETHEUS_BODY_BYTES);
        assert!(exposition.ends_with('\n'));
    }

    #[test]
    fn redaction_handles_common_secret_forms_without_erasing_context() {
        let input = "authorization=Bearer abc123 password: 'p@ss' tenant=public token=xyz client_secret=hidden";
        let output = redact_diagnostic(input);
        assert!(output.contains("authorization=[REDACTED]"));
        assert!(output.contains("password: '[REDACTED]'"));
        assert!(output.contains("tenant=public"));
        assert!(!output.contains("abc123"));
        assert!(!output.contains("p@ss"));
        assert!(!output.contains("xyz"));
        assert!(!output.contains("hidden"));
    }

    #[test]
    fn redaction_handles_url_query_secrets() {
        let output = redact_diagnostic("GET /health?token=abc&region=GRU");
        assert!(output.contains("token=[REDACTED]&region=GRU"));
        assert!(!output.contains("abc"));
    }

    #[test]
    fn redaction_handles_json_headers_hyphenated_keys_and_escaped_quotes() {
        let input = concat!(
            r#"{"password":"p\"ass","token":"json-token"} "#,
            "Authorization: Basic Zm9vOmJhcg==; ",
            "x-api-key=header-key cookie=session=browser-secret"
        );
        let output = redact_diagnostic(input);
        for secret in ["p\\\"ass", "json-token", "Zm9vOmJhcg==", "header-key", "browser-secret"] {
            assert!(!output.contains(secret), "secret remained in {output}");
        }
        assert!(output.contains(r#""password":"[REDACTED]""#));
        assert!(output.contains("Authorization: [REDACTED]"));
        assert!(output.contains("x-api-key=[REDACTED]"));
        assert!(output.contains("cookie=[REDACTED]"));
    }

    #[test]
    fn redaction_does_not_rewrite_words_that_only_contain_a_marker() {
        let input = "tokenizer ready; passwordless mode; secretive status";
        assert_eq!(redact_diagnostic(input), input);
    }

    #[test]
    fn redaction_classifies_namespaced_secret_keys_case_insensitively() {
        let input = concat!(
            "db_password=db-secret oauth_token:oauth-secret ",
            "DB.PASSWORD=dot-secret service:client_secret='client-secret' ",
            "x-api-key=header-secret nested:private-key=private-secret"
        );
        let output = redact_diagnostic(input);
        for secret in [
            "db-secret",
            "oauth-secret",
            "dot-secret",
            "client-secret",
            "header-secret",
            "private-secret",
        ] {
            assert!(!output.contains(secret), "secret remained in {output}");
        }
        assert!(output.contains("db_password=[REDACTED]"));
        assert!(output.contains("oauth_token:[REDACTED]"));
        assert!(output.contains("service:client_secret='[REDACTED]'"));
    }

    #[test]
    fn redaction_handles_unicode_boundaries_and_format_tricks() {
        let input = concat!(
            "DB_PASSWORD\u{200d}=zero-width-secret ",
            "oauth\u{2060}_token=word-joiner-secret ",
            "password\u{00a0}=nonbreaking-space-secret ",
            "tenant=public"
        );
        let output = redact_diagnostic(input);
        for secret in ["zero-width-secret", "word-joiner-secret", "nonbreaking-space-secret"] {
            assert!(!output.contains(secret), "secret remained in {output}");
        }
        assert!(output.contains("tenant=public"));
    }

    #[test]
    fn redaction_handles_header_delimiter_adjacency() {
        let output = redact_diagnostic("Authorization:Bearer header-secret X-Trace: kept");
        assert!(!output.contains("header-secret"));
        assert!(output.contains("Authorization:[REDACTED]"));
        assert!(output.contains("X-Trace: kept"));
    }

    #[test]
    fn redaction_handles_duplicate_keys_and_adjacent_delimiters_in_one_pass() {
        let input = "password=one&password=two;oauth_token:three,tenant=public document=visible";
        let output = redact_diagnostic(input);
        for secret in ["one", "two", "three"] {
            assert!(!output.contains(secret), "secret remained in {output}");
        }
        assert!(output.contains("password=[REDACTED]&password=[REDACTED]"));
        assert!(output.contains("tenant=public document=visible"));
    }

    #[test]
    fn redaction_bounds_pathological_diagnostic_text() {
        let input = "tenant=public ".repeat(MAX_DIAGNOSTIC_BYTES);
        let output = redact_diagnostic(&input);
        assert!(output.len() <= MAX_DIAGNOSTIC_BYTES);
        assert!(output.contains("tenant=public"));
    }
}
