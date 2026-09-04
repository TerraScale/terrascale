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
//!
//! E-14 start per D-72 ("metrics stay cheap"): no metric carries a
//! per-database, per-tenant, or per-view label.  Cardinality is bounded by
//! host (the Prometheus `instance` target) times closed Region and subsystem
//! vocabularies, so the Grafana Cloud free tier holds.  Per-database and
//! per-tenant numbers are engine usage facts (D-42), queried from the
//! control-plane database rather than scraped.  Structured logs use the
//! [`LogRecord`] schema, are sampled above [`LOG_SAMPLE_ABOVE_LINES_PER_SEC_PER_HOST`]
//! lines/s per host, and are retained [`LOG_RETENTION_DAYS`] days; both bounds
//! are enforced by the `ts-orchestrator` host agent, not in process.

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

/// The measured clock-offset magnitude, in seconds, above which a member
/// refuses new writes (D-69).
///
/// Chrony disciplines the host clock against OCI NTP; the member exports its
/// measured offset via `terrabase_clock_offset_seconds` and never mints an
/// HLC from a suspect clock. The alert threshold in
/// `deploy/observability/prometheus-rules.yml` sits below this refusal line
/// so operators get headroom before writes stop.
pub const CLOCK_OFFSET_WRITE_REFUSAL_THRESHOLD_SECONDS: f64 = 0.5;

/// Monthly billable egress per Region, in bytes, that pages the operator
/// view (D-66).
///
/// Replication journal frames, bootstrap snapshots, and direct member reads
/// leave OCI as billable outbound; 8 decimal TB per Region per month is the
/// Grafana alert line. The host `instance` target plus the closed Region and
/// [`EgressKind`] vocabularies keep this family's cardinality bounded.
pub const EGRESS_MONTHLY_ALERT_BYTES: u64 = 8_000_000_000_000;

/// The per-host structured-log rate, in lines/s, above which the host agent
/// samples before shipping (D-72).
///
/// Sampling is enforced by the `ts-orchestrator` Grafana-agent configuration,
/// not in process: the engine always emits [`LogRecord`] lines and never
/// drops a caller-visible error to meet this budget.
pub const LOG_SAMPLE_ABOVE_LINES_PER_SEC_PER_HOST: u64 = 100;

/// Structured-log retention, in days, enforced by the `ts-orchestrator`
/// log-agent configuration (D-72).
pub const LOG_RETENTION_DAYS: u32 = 14;

/// The maximum UTF-8 byte length retained in one structured-log message.
///
/// This is deliberately smaller than [`MAX_DIAGNOSTIC_BYTES`]: the latter is
/// the amount inspected by the redactor, while this is the amount allowed to
/// cross the log-emitter boundary.
pub const MAX_LOG_MESSAGE_BYTES: usize = 16 * 1024;

/// The maximum byte length of a closed-schema string field.
pub const MAX_LOG_FIELD_BYTES: usize = 64;

/// The fixed upper bound on egress-kind label values.
pub const MAX_EGRESS_KIND_LABEL_VALUES: usize = EGRESS_KINDS.len();

/// The maximum number of egress counter series.
///
/// Each series is one member of the closed Region and egress-kind product,
/// scraped per host `instance`. No runtime string can increase this value.
pub const MAX_EGRESS_SERIES: usize = MAX_REGION_LABEL_VALUES * MAX_EGRESS_KIND_LABEL_VALUES;

/// The maximum number of clock series: one offset gauge plus one write-refusal
/// counter, both unlabeled because the host is the Prometheus `instance`.
pub const MAX_CLOCK_SERIES: usize = 2;

/// A closed subsystem vocabulary for adapter-facing observations.
///
/// This is deliberately separate from [`Component`]. Components describe
/// readiness; subsystems describe where a bounded event happened. It is the
/// only subsystem value accepted by the generic adapter hooks, so database,
/// tenant, view, document, and query identifiers cannot become labels.
#[derive(Clone, Copy, Debug, Eq, Ord, PartialEq, PartialOrd)]
pub enum Subsystem {
    /// SQLite pages, WAL and storage accounting.
    Storage,
    /// Group commit and durability.
    Commit,
    /// Read-only SQL parsing and execution.
    Sql,
    /// Automatic-index planning and lifecycle.
    Index,
    /// Reactive and collection synchronization.
    Sync,
    /// Cross-Region replication and repair.
    Replication,
    /// Backup and restore workers.
    Backup,
    /// Authoritative usage-fact publication.
    Usage,
    /// Host clock discipline and write refusal.
    Clock,
    /// Direct OCI member egress.
    Egress,
    /// Admission and abuse guards.
    Guard,
}

impl Subsystem {
    /// Returns the stable Prometheus/log value.
    #[must_use]
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::Storage => "storage",
            Self::Commit => "commit",
            Self::Sql => "sql",
            Self::Index => "index",
            Self::Sync => "sync",
            Self::Replication => "replication",
            Self::Backup => "backup",
            Self::Usage => "usage",
            Self::Clock => "clock",
            Self::Egress => "egress",
            Self::Guard => "guard",
        }
    }
}

/// Every admitted subsystem value in stable order.
pub const SUBSYSTEMS: [Subsystem; 11] = [
    Subsystem::Storage,
    Subsystem::Commit,
    Subsystem::Sql,
    Subsystem::Index,
    Subsystem::Sync,
    Subsystem::Replication,
    Subsystem::Backup,
    Subsystem::Usage,
    Subsystem::Clock,
    Subsystem::Egress,
    Subsystem::Guard,
];

/// A closed guard category. Guard hits are aggregate facts, never customer
/// identifiers or request parameters.
#[derive(Clone, Copy, Debug, Eq, Ord, PartialEq, PartialOrd)]
pub enum GuardKind {
    /// Request-rate or concurrency guard.
    Rate,
    /// Direct member egress guard.
    Egress,
    /// Storage or logical-byte guard.
    Storage,
    /// Write or queue budget guard.
    Capacity,
    /// Clock-offset safety guard.
    Clock,
}

impl GuardKind {
    /// Returns the stable label value.
    #[must_use]
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::Rate => "rate",
            Self::Egress => "egress",
            Self::Storage => "storage",
            Self::Capacity => "capacity",
            Self::Clock => "clock",
        }
    }
}

/// Every admitted guard category in stable order.
pub const GUARD_KINDS: [GuardKind; 5] =
    [GuardKind::Rate, GuardKind::Egress, GuardKind::Storage, GuardKind::Capacity, GuardKind::Clock];

/// A typed, bounded label accepted by adapter integrations.
///
/// Host is intentionally an opaque deployment value (the Prometheus target
/// supplies it as `instance`); Region and subsystem are closed enums. There
/// is no string-valued constructor for a label, making high-cardinality IDs a
/// compile-time/API-boundary concern instead of a renderer convention.
#[derive(Clone, Copy, Debug, Eq, Ord, PartialEq, PartialOrd)]
pub enum BoundedLabel {
    /// The deployment host is supplied by the scrape target.
    Host,
    /// A closed TerraScale Region.
    Region(Region),
    /// A closed engine subsystem.
    Subsystem(Subsystem),
}

/// Returns whether a textual label key is forbidden because it identifies a
/// customer or an individual operation. This helper is useful at integration
/// boundaries that receive external metadata before converting it to enums.
#[must_use]
pub fn is_high_cardinality_label_name(name: &str) -> bool {
    matches!(
        name.to_ascii_lowercase().as_str(),
        "db" | "database"
            | "database_id"
            | "tenant"
            | "tenant_id"
            | "view"
            | "view_id"
            | "document"
            | "document_id"
            | "doc"
            | "doc_id"
            | "query"
            | "query_id"
            | "request"
            | "request_id"
            | "collection"
            | "collection_id"
    )
}

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
            Self::Other => "other",
        }
    }
}

/// Every admitted operation label in stable order.
pub const OPERATIONS: [Operation; 8] = [
    Operation::Read,
    Operation::Write,
    Operation::Query,
    Operation::Subscribe,
    Operation::Backup,
    Operation::Restore,
    Operation::Repair,
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

/// A bounded billable-egress class (D-66).
///
/// Replication journal frames, bootstrap snapshots, and direct member reads
/// each bill as OCI outbound at the source Region's rate; CDN fills and
/// reader-side delivery are Cloudflare's and stay out of this family. The
/// three classes keep per-Region spend attributable without a
/// per-database, per-tenant, or per-view label (D-72).
#[derive(Clone, Copy, Debug, Eq, Ord, PartialEq, PartialOrd)]
pub enum EgressKind {
    /// Cross-Region journal frames over member-to-member QUIC.
    Replication,
    /// Bootstrap snapshots for a new follower or Region.
    Snapshot,
    /// Direct member reads served from OCI rather than the CDN.
    Read,
}

impl EgressKind {
    /// Returns the stable label value for this egress class.
    #[must_use]
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::Replication => "replication",
            Self::Snapshot => "snapshot",
            Self::Read => "read",
        }
    }
}

/// Every admitted egress-kind label in stable order.
pub const EGRESS_KINDS: [EgressKind; 3] =
    [EgressKind::Replication, EgressKind::Snapshot, EgressKind::Read];

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
            Self::Backup => "backup",
            Self::UsageExport => "usage_export",
        }
    }
}

/// Every admitted queue label in stable order.
pub const QUEUES: [Queue; 5] =
    [Queue::Foreground, Queue::Commit, Queue::Replication, Queue::Backup, Queue::UsageExport];

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
        if self.is_ready() { 200 } else { 503 }
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

#[derive(Clone, Copy, Debug, Eq, Ord, PartialEq, PartialOrd)]
struct SubsystemKey {
    subsystem: Subsystem,
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

/// One aggregate operation observation for a closed subsystem.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct SubsystemSeries {
    subsystem: Subsystem,
    outcome: Outcome,
    total: u64,
    duration: HistogramSnapshot,
}

impl SubsystemSeries {
    /// Returns the closed subsystem label.
    #[must_use]
    pub const fn subsystem(&self) -> Subsystem {
        self.subsystem
    }

    /// Returns the closed outcome label.
    #[must_use]
    pub const fn outcome(&self) -> Outcome {
        self.outcome
    }

    /// Returns the aggregate operation count.
    #[must_use]
    pub const fn total(&self) -> u64 {
        self.total
    }

    /// Returns the aggregate duration histogram.
    #[must_use]
    pub const fn duration(&self) -> &HistogramSnapshot {
        &self.duration
    }
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
/// at most once. This type carries no database, tenant, view, collection,
/// document, query, path, credential, provider, or free-form diagnostic value.
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
    active_members: Vec<ActiveMembers>,
    component_health: Vec<ComponentHealth>,
    backup_last_success_age: Vec<(BackupMode, f64)>,
    backup_runs: Vec<(BackupMode, TaskOutcome, u64)>,
    usage_export_frontier: Option<u64>,
    usage_export_failures: u64,
    clock_offset_seconds: Option<f64>,
    clock_write_refusals: u64,
    egress_bytes: Vec<(Region, EgressKind, u64)>,
    subsystem_operations: Vec<SubsystemSeries>,
    guard_hits: Vec<(GuardKind, u64)>,
    storage_bytes: Option<u64>,
    storage_capacity_bytes: Option<u64>,
    storage_errors: u64,
    active_indexes: Option<u64>,
    index_regressions: u64,
    sync_backlog_bytes: Option<u64>,
    sync_active_views: Option<u64>,
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

    /// Returns the measured host clock offset against OCI NTP in seconds,
    /// when observed (D-69).
    ///
    /// The value is signed: a negative offset means the host clock trails.
    /// The series is absent until the collector publishes its first
    /// observation. Crossing [`CLOCK_OFFSET_WRITE_REFUSAL_THRESHOLD_SECONDS`]
    /// in magnitude refuses new writes.
    #[must_use]
    pub const fn clock_offset_seconds(&self) -> Option<f64> {
        self.clock_offset_seconds
    }

    /// Returns new writes refused because the measured clock offset exceeded
    /// [`CLOCK_OFFSET_WRITE_REFUSAL_THRESHOLD_SECONDS`] (D-69).
    #[must_use]
    pub const fn clock_write_refusals(&self) -> u64 {
        self.clock_write_refusals
    }

    /// Returns `(region, kind, bytes)` billable egress counters in stable
    /// Region and egress-kind order (D-66).
    #[must_use]
    pub fn egress_bytes(&self) -> &[(Region, EgressKind, u64)] {
        &self.egress_bytes
    }

    /// Returns aggregate operation observations for storage, SQL, indexes,
    /// sync, replication, backup, usage, clock, egress, and guard adapters.
    #[must_use]
    pub fn subsystem_operations(&self) -> &[SubsystemSeries] {
        &self.subsystem_operations
    }

    /// Returns aggregate abuse/safety guard hits in stable guard order.
    #[must_use]
    pub fn guard_hits(&self) -> &[(GuardKind, u64)] {
        &self.guard_hits
    }

    /// Returns current logical storage bytes, when a storage collector has
    /// published a finite observation.
    #[must_use]
    pub const fn storage_bytes(&self) -> Option<u64> {
        self.storage_bytes
    }

    /// Returns configured storage capacity, when known.
    #[must_use]
    pub const fn storage_capacity_bytes(&self) -> Option<u64> {
        self.storage_capacity_bytes
    }

    /// Returns storage errors observed by this member.
    #[must_use]
    pub const fn storage_errors(&self) -> u64 {
        self.storage_errors
    }

    /// Returns the number of currently visible indexes, when known.
    #[must_use]
    pub const fn active_indexes(&self) -> Option<u64> {
        self.active_indexes
    }

    /// Returns automatic-index regressions observed by this member.
    #[must_use]
    pub const fn index_regressions(&self) -> u64 {
        self.index_regressions
    }

    /// Returns queued synchronization bytes, when known.
    #[must_use]
    pub const fn sync_backlog_bytes(&self) -> Option<u64> {
        self.sync_backlog_bytes
    }

    /// Returns the aggregate attached-view count, when known. Individual view
    /// IDs are intentionally not represented.
    #[must_use]
    pub const fn sync_active_views(&self) -> Option<u64> {
        self.sync_active_views
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
        write_clock_metrics(&mut output, self);
        write_egress_metrics(&mut output, self);
        write_subsystem_metrics(&mut output, self);
        write_storage_index_sync_metrics(&mut output, self);
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
    active_members: BTreeMap<Region, u64>,
    component_health: BTreeMap<Component, HealthStatus>,
    backup_last_success_age_seconds: BTreeMap<BackupMode, f64>,
    backup_runs: BTreeMap<(BackupMode, TaskOutcome), u64>,
    usage_export_frontier: Option<u64>,
    usage_export_failures: u64,
    clock_offset_seconds: Option<f64>,
    clock_write_refusals: u64,
    egress_bytes: BTreeMap<(Region, EgressKind), u64>,
    subsystem_totals: BTreeMap<SubsystemKey, u64>,
    subsystem_durations: BTreeMap<SubsystemKey, Histogram>,
    guard_hits: BTreeMap<GuardKind, u64>,
    storage_bytes: Option<u64>,
    storage_capacity_bytes: Option<u64>,
    storage_errors: u64,
    active_indexes: Option<u64>,
    index_regressions: u64,
    sync_backlog_bytes: Option<u64>,
    sync_active_views: Option<u64>,
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

/// Error returned when an integration attempts to construct an unbounded
/// label set. Values are intentionally not included in the error, avoiding a
/// second accidental diagnostic leak.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum LabelBoundaryError {
    /// The key is a database, tenant, view, document, query, or request ID.
    HighCardinality,
    /// The key is not one of `host`, `region`, or `subsystem`.
    Unsupported,
    /// A closed value was malformed or not admitted for this deployment.
    InvalidValue,
    /// A key appeared more than once.
    Duplicate,
}

/// A label set safe to pass to a Prometheus/log adapter.
///
/// Host is represented only by presence because the scrape target supplies
/// its value as `instance`. Region and subsystem are closed enums. This type
/// is useful for adapters that receive metadata from another process and
/// need a single fail-closed conversion point.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct BoundedLabelSet {
    host: bool,
    region: Option<Region>,
    subsystem: Option<Subsystem>,
}

impl BoundedLabelSet {
    /// Constructs a label set from closed values.
    #[must_use]
    pub const fn new(host: bool, region: Option<Region>, subsystem: Option<Subsystem>) -> Self {
        Self { host, region, subsystem }
    }

    /// Converts textual adapter metadata while rejecting high-cardinality
    /// names and values that are not in the closed vocabulary.
    pub fn try_from_pairs(pairs: &[(&str, &str)]) -> Result<Self, LabelBoundaryError> {
        let mut labels = Self::new(false, None, None);
        for (name, value) in pairs {
            if is_high_cardinality_label_name(name) {
                return Err(LabelBoundaryError::HighCardinality);
            }
            match *name {
                "host" => {
                    if labels.host {
                        return Err(LabelBoundaryError::Duplicate);
                    }
                    // Host value is deliberately never retained or rendered.
                    if value.is_empty() || value.len() > MAX_LOG_FIELD_BYTES {
                        return Err(LabelBoundaryError::InvalidValue);
                    }
                    labels.host = true;
                }
                "region" => {
                    if labels.region.is_some() {
                        return Err(LabelBoundaryError::Duplicate);
                    }
                    let region = Region::from_code(value);
                    if region == Region::Unknown && *value != "unknown" {
                        return Err(LabelBoundaryError::InvalidValue);
                    }
                    labels.region = Some(region);
                }
                "subsystem" => {
                    if labels.subsystem.is_some() {
                        return Err(LabelBoundaryError::Duplicate);
                    }
                    labels.subsystem =
                        Some(subsystem_from_str(value).ok_or(LabelBoundaryError::InvalidValue)?);
                }
                _ => return Err(LabelBoundaryError::Unsupported),
            }
        }
        Ok(labels)
    }

    /// Whether the scrape target supplies a host label.
    #[must_use]
    pub const fn has_host(&self) -> bool {
        self.host
    }

    /// Returns the optional closed Region.
    #[must_use]
    pub const fn region(&self) -> Option<Region> {
        self.region
    }

    /// Returns the optional closed subsystem.
    #[must_use]
    pub const fn subsystem(&self) -> Option<Subsystem> {
        self.subsystem
    }
}

fn subsystem_from_str(value: &str) -> Option<Subsystem> {
    SUBSYSTEMS.iter().copied().find(|subsystem| subsystem.as_str() == value)
}

/// Read-only source implemented by private Prometheus adapters.
pub trait MetricsSource {
    /// Takes one immutable, internally consistent snapshot.
    fn metrics_snapshot(&self) -> MetricsSnapshot;

    /// Renders one bounded exposition body, or rejects it if a future schema
    /// expansion exceeds the configured response limit.
    fn metrics_body(&self) -> Result<String, ExpositionTooLarge> {
        self.metrics_snapshot().try_render_prometheus()
    }
}

impl MetricsSource for MetricsRegistry {
    fn metrics_snapshot(&self) -> MetricsSnapshot {
        self.snapshot()
    }
}

/// Sink implemented by a process's structured-log adapter.
pub trait LogSink {
    /// Accepts one already-redacted record. Implementations must not add
    /// customer identifiers as fields.
    fn emit(&self, record: &LogRecord);
}

/// The narrow wiring surface shared by storage/server/replication adapters.
///
/// It deliberately exposes no method accepting a database, tenant, view,
/// document, query, or request ID. Each subsystem can report bounded outcomes
/// and durations through this value without knowing the exposition format.
pub struct ObservabilityHooks<'a> {
    metrics: &'a MetricsRegistry,
}

impl<'a> ObservabilityHooks<'a> {
    /// Creates adapter hooks backed by one registry.
    #[must_use]
    pub const fn new(metrics: &'a MetricsRegistry) -> Self {
        Self { metrics }
    }

    /// Returns the backing registry for specialized existing hooks.
    #[must_use]
    pub const fn metrics(&self) -> &'a MetricsRegistry {
        self.metrics
    }

    /// Records a bounded subsystem operation.
    pub fn operation(&self, subsystem: Subsystem, outcome: Outcome, duration: Duration) {
        self.metrics.record_subsystem(subsystem, outcome, duration);
    }

    /// Records a bounded guard hit.
    pub fn guard(&self, kind: GuardKind) {
        self.metrics.record_guard_hit(kind);
    }
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

    /// Records one bounded operation in a named subsystem.
    ///
    /// This is the integration hook for storage, SQL, index, sync,
    /// replication, backup, usage, clock, direct-egress, and guard adapters.
    /// It accepts only closed enums and therefore cannot create a series for a
    /// database, tenant, view, document, query, or request identifier.
    pub fn record_subsystem(&self, subsystem: Subsystem, outcome: Outcome, duration: Duration) {
        let key = SubsystemKey { subsystem, outcome };
        let mut state = lock(&self.state);
        saturating_increment(state.subsystem_totals.entry(key).or_default());
        state.subsystem_durations.entry(key).or_default().observe(duration);
    }

    /// Records a storage operation without admitting a storage identifier.
    pub fn record_storage(&self, outcome: Outcome, duration: Duration) {
        self.record_subsystem(Subsystem::Storage, outcome, duration);
    }

    /// Records a SQL operation without admitting statement text or query IDs.
    pub fn record_sql(&self, outcome: Outcome, duration: Duration) {
        self.record_subsystem(Subsystem::Sql, outcome, duration);
    }

    /// Records an automatic-index operation without admitting index names or
    /// collection identifiers.
    pub fn record_index(&self, outcome: Outcome, duration: Duration) {
        self.record_subsystem(Subsystem::Index, outcome, duration);
    }

    /// Records one synchronization operation without admitting a view ID.
    pub fn record_sync(&self, outcome: Outcome, duration: Duration) {
        self.record_subsystem(Subsystem::Sync, outcome, duration);
    }

    /// Records one bounded guard hit. Guard categories are closed and contain
    /// no customer or request identity.
    pub fn record_guard_hit(&self, guard: GuardKind) {
        let mut state = lock(&self.state);
        saturating_increment(state.guard_hits.entry(guard).or_default());
    }

    /// Sets current logical bytes occupied by SQLite data and retained
    /// history. Non-finite values cannot enter because the value is integral.
    pub fn set_storage_bytes(&self, bytes: u64) {
        lock(&self.state).storage_bytes = Some(bytes);
    }

    /// Sets the configured storage capacity. A missing observation remains
    /// unknown and is not rendered as unlimited capacity.
    pub fn set_storage_capacity_bytes(&self, bytes: u64) {
        lock(&self.state).storage_capacity_bytes = Some(bytes);
    }

    /// Clears storage gauges when the collector loses its source.
    pub fn clear_storage_capacity(&self) {
        let mut state = lock(&self.state);
        state.storage_bytes = None;
        state.storage_capacity_bytes = None;
    }

    /// Records one storage failure without retaining its path or database ID.
    pub fn record_storage_error(&self) {
        let mut state = lock(&self.state);
        state.storage_errors = state.storage_errors.saturating_add(1);
    }

    /// Sets the number of currently visible automatic indexes.
    pub fn set_active_indexes(&self, count: u64) {
        lock(&self.state).active_indexes = Some(count);
    }

    /// Clears the automatic-index count when its collector is unavailable.
    pub fn clear_active_indexes(&self) {
        lock(&self.state).active_indexes = None;
    }

    /// Records an automatic-index regression. The affected template and
    /// collection are deliberately not accepted at this boundary.
    pub fn record_index_regression(&self) {
        let mut state = lock(&self.state);
        state.index_regressions = state.index_regressions.saturating_add(1);
    }

    /// Sets aggregate synchronization backlog bytes.
    pub fn set_sync_backlog_bytes(&self, bytes: u64) {
        lock(&self.state).sync_backlog_bytes = Some(bytes);
    }

    /// Sets aggregate attached synchronization views, never individual IDs.
    pub fn set_sync_active_views(&self, count: u64) {
        lock(&self.state).sync_active_views = Some(count);
    }

    /// Clears aggregate synchronization gauges when the collector is stale.
    pub fn clear_sync_gauges(&self) {
        let mut state = lock(&self.state);
        state.sync_backlog_bytes = None;
        state.sync_active_views = None;
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

    /// Sets the measured host clock offset against OCI NTP, in seconds
    /// (D-69).
    ///
    /// The value is signed: negative means the host clock trails. Non-finite
    /// observations are refused and leave the prior observation intact, so a
    /// broken collector cannot poison the refusal comparison.
    pub fn set_clock_offset_seconds(&self, offset_seconds: f64) {
        if !offset_seconds.is_finite() {
            return;
        }
        lock(&self.state).clock_offset_seconds = Some(offset_seconds);
    }

    /// Clears the measured host clock offset when its collector reports an
    /// unknown or stale sample. The gauge is absent until the next finite
    /// observation arrives.
    pub fn clear_clock_offset_seconds(&self) {
        lock(&self.state).clock_offset_seconds = None;
    }

    /// Records one new write refused because the measured clock offset
    /// exceeded [`CLOCK_OFFSET_WRITE_REFUSAL_THRESHOLD_SECONDS`] in magnitude
    /// (D-69).
    pub fn record_clock_write_refusal(&self) {
        let mut state = lock(&self.state);
        state.clock_write_refusals = state.clock_write_refusals.saturating_add(1);
    }

    /// Adds billable egress bytes for one bounded Region and egress class
    /// (D-66).
    ///
    /// Callers report OCI outbound only: replication journal frames, bootstrap
    /// snapshots, and direct member reads. CDN fills and reader-side delivery
    /// are Cloudflare's and must not enter this counter.
    pub fn record_egress_bytes(&self, region: Region, kind: EgressKind, bytes: u64) {
        let mut state = lock(&self.state);
        let total = state.egress_bytes.entry((region, kind)).or_default();
        *total = total.saturating_add(bytes);
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
        let egress_bytes = state
            .egress_bytes
            .iter()
            .map(|((region, kind), bytes)| (*region, *kind, *bytes))
            .collect();
        let subsystem_operations = state
            .subsystem_totals
            .iter()
            .map(|(key, total)| SubsystemSeries {
                subsystem: key.subsystem,
                outcome: key.outcome,
                total: *total,
                duration: HistogramSnapshot::from_histogram(
                    state.subsystem_durations.get(key).copied().unwrap_or_default(),
                ),
            })
            .collect();
        let guard_hits = state.guard_hits.iter().map(|(guard, total)| (*guard, *total)).collect();
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
            active_members,
            component_health,
            backup_last_success_age,
            backup_runs,
            usage_export_frontier: state.usage_export_frontier,
            usage_export_failures: state.usage_export_failures,
            clock_offset_seconds: state.clock_offset_seconds,
            clock_write_refusals: state.clock_write_refusals,
            egress_bytes,
            subsystem_operations,
            guard_hits,
            storage_bytes: state.storage_bytes,
            storage_capacity_bytes: state.storage_capacity_bytes,
            storage_errors: state.storage_errors,
            active_indexes: state.active_indexes,
            index_regressions: state.index_regressions,
            sync_backlog_bytes: state.sync_backlog_bytes,
            sync_active_views: state.sync_active_views,
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
        "gauge",
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
    let separator = if labels.is_empty() { "" } else { "," };
    for (index, upper_bound) in REQUEST_LATENCY_BUCKETS.iter().enumerate() {
        writeln!(
            output,
            "{name}_bucket{{{labels}{separator}le=\"{}\"}} {}",
            format_bound(*upper_bound),
            histogram.cumulative_buckets[index]
        )
        .expect("writing String cannot fail");
    }
    writeln!(output, "{name}_bucket{{{labels}{separator}le=\"+Inf\"}} {}", histogram.count)
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

fn write_clock_metrics(output: &mut String, snapshot: &MetricsSnapshot) {
    write_help_type(
        output,
        "terrabase_clock_offset_seconds",
        "Measured host clock offset against OCI NTP in seconds; new writes are refused past 0.5 in magnitude.",
        "gauge",
    );
    if let Some(offset) = snapshot.clock_offset_seconds {
        writeln!(output, "terrabase_clock_offset_seconds {}", format_float(offset))
            .expect("writing String cannot fail");
    }
    write_help_type(
        output,
        "terrabase_clock_write_refusals_total",
        "New writes refused because the measured clock offset exceeded 0.5 seconds in magnitude.",
        "counter",
    );
    writeln!(output, "terrabase_clock_write_refusals_total {}", snapshot.clock_write_refusals)
        .expect("writing String cannot fail");
}

fn write_egress_metrics(output: &mut String, snapshot: &MetricsSnapshot) {
    write_help_type(
        output,
        "terrabase_egress_bytes_total",
        "Billable OCI outbound bytes by bounded Region and egress class.",
        "counter",
    );
    for (region, kind, bytes) in &snapshot.egress_bytes {
        writeln!(
            output,
            "terrabase_egress_bytes_total{{region=\"{}\",kind=\"{}\"}} {bytes}",
            region.as_str(),
            kind.as_str()
        )
        .expect("writing String cannot fail");
    }
}

fn write_subsystem_metrics(output: &mut String, snapshot: &MetricsSnapshot) {
    write_help_type(
        output,
        "terrabase_subsystem_operations_total",
        "Aggregate subsystem operations by closed subsystem and outcome.",
        "counter",
    );
    let total = snapshot
        .subsystem_operations
        .iter()
        .map(|observation| observation.total)
        .fold(0_u64, u64::saturating_add);
    writeln!(output, "terrabase_subsystem_operations_total {total}")
        .expect("writing String cannot fail");
    write_help_type(
        output,
        "terrabase_subsystem_operation_duration_seconds",
        "Aggregate subsystem operation duration in seconds by closed subsystem and outcome.",
        "gauge",
    );
    let mut duration = Histogram::default();
    for observation in &snapshot.subsystem_operations {
        duration.count = duration.count.saturating_add(observation.duration.count());
        duration.sum_seconds += observation.duration.sum_seconds();
        for (index, count) in observation.duration.cumulative_buckets().iter().enumerate() {
            // Convert cumulative snapshots back to per-bucket counts before
            // adding, then let the renderer produce cumulative values.
            let previous =
                if index == 0 { 0 } else { observation.duration.cumulative_buckets()[index - 1] };
            duration.buckets[index] =
                duration.buckets[index].saturating_add(count.saturating_sub(previous));
        }
    }
    writeln!(
        output,
        "terrabase_subsystem_operation_duration_seconds {}",
        format_float(duration.sum_seconds)
    )
    .expect("writing String cannot fail");
    // Stable aliases make the most important lanes discoverable without
    // creating separate, potentially unbounded registries in each adapter.
    for (name, subsystem, help) in [
        (
            "terrabase_sql_queries_total",
            Subsystem::Sql,
            "Read-only SQL operations by bounded outcome.",
        ),
        (
            "terrabase_index_operations_total",
            Subsystem::Index,
            "Automatic-index operations by bounded outcome.",
        ),
        (
            "terrabase_sync_operations_total",
            Subsystem::Sync,
            "Synchronization operations by bounded outcome.",
        ),
    ] {
        write_help_type(output, name, help, "counter");
        let total = snapshot
            .subsystem_operations
            .iter()
            .filter(|observation| observation.subsystem == subsystem)
            .map(|observation| observation.total)
            .fold(0_u64, u64::saturating_add);
        writeln!(output, "{name} {total}").expect("writing String cannot fail");
    }
    for (name, subsystem, help) in [
        (
            "terrabase_sql_query_duration_seconds",
            Subsystem::Sql,
            "Read-only SQL duration in seconds by bounded outcome.",
        ),
        (
            "terrabase_index_operation_duration_seconds",
            Subsystem::Index,
            "Automatic-index operation duration in seconds by bounded outcome.",
        ),
        (
            "terrabase_sync_operation_duration_seconds",
            Subsystem::Sync,
            "Synchronization operation duration in seconds by bounded outcome.",
        ),
    ] {
        write_help_type(output, name, help, "gauge");
        let mut duration = Histogram::default();
        for observation in snapshot
            .subsystem_operations
            .iter()
            .filter(|observation| observation.subsystem == subsystem)
        {
            duration.count = duration.count.saturating_add(observation.duration.count());
            duration.sum_seconds += observation.duration.sum_seconds();
            for (index, count) in observation.duration.cumulative_buckets().iter().enumerate() {
                let previous = if index == 0 {
                    0
                } else {
                    observation.duration.cumulative_buckets()[index - 1]
                };
                duration.buckets[index] =
                    duration.buckets[index].saturating_add(count.saturating_sub(previous));
            }
        }
        writeln!(output, "{name} {}", format_float(duration.sum_seconds))
            .expect("writing String cannot fail");
    }
    write_help_type(
        output,
        "terrabase_guard_hits_total",
        "Admission and abuse guard hits by closed guard category.",
        "counter",
    );
    let total =
        snapshot.guard_hits.iter().map(|(_, total)| *total).fold(0_u64, u64::saturating_add);
    writeln!(output, "terrabase_guard_hits_total {total}").expect("writing String cannot fail");
}

fn write_storage_index_sync_metrics(output: &mut String, snapshot: &MetricsSnapshot) {
    write_help_type(
        output,
        "terrabase_storage_bytes",
        "Current logical SQLite storage bytes including retained history.",
        "gauge",
    );
    if let Some(bytes) = snapshot.storage_bytes {
        writeln!(output, "terrabase_storage_bytes {bytes}").expect("writing String cannot fail");
    }
    write_help_type(
        output,
        "terrabase_storage_capacity_bytes",
        "Configured SQLite storage capacity in bytes.",
        "gauge",
    );
    if let Some(bytes) = snapshot.storage_capacity_bytes {
        writeln!(output, "terrabase_storage_capacity_bytes {bytes}")
            .expect("writing String cannot fail");
    }
    write_help_type(
        output,
        "terrabase_storage_errors_total",
        "Storage failures observed by this member.",
        "counter",
    );
    writeln!(output, "terrabase_storage_errors_total {}", snapshot.storage_errors)
        .expect("writing String cannot fail");

    write_help_type(
        output,
        "terrabase_active_indexes",
        "Currently visible automatic indexes.",
        "gauge",
    );
    if let Some(count) = snapshot.active_indexes {
        writeln!(output, "terrabase_active_indexes {count}").expect("writing String cannot fail");
    }
    write_help_type(
        output,
        "terrabase_index_regressions_total",
        "Automatic-index regressions that caused a lifecycle response.",
        "counter",
    );
    writeln!(output, "terrabase_index_regressions_total {}", snapshot.index_regressions)
        .expect("writing String cannot fail");

    write_help_type(
        output,
        "terrabase_sync_backlog_bytes",
        "Aggregate synchronization backlog bytes.",
        "gauge",
    );
    if let Some(bytes) = snapshot.sync_backlog_bytes {
        writeln!(output, "terrabase_sync_backlog_bytes {bytes}")
            .expect("writing String cannot fail");
    }
    write_help_type(
        output,
        "terrabase_sync_active_views",
        "Aggregate attached synchronization views.",
        "gauge",
    );
    if let Some(count) = snapshot.sync_active_views {
        writeln!(output, "terrabase_sync_active_views {count}")
            .expect("writing String cannot fail");
    }
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

/// The structured-log field name for the millisecond Unix timestamp.
pub const LOG_FIELD_TIMESTAMP: &str = "ts";
/// The structured-log field name for the [`LogLevel`] value.
pub const LOG_FIELD_LEVEL: &str = "level";
/// The structured-log field name for the [`Component`] value.
pub const LOG_FIELD_COMPONENT: &str = "component";
/// The structured-log field name for the [`LogEvent`] value.
pub const LOG_FIELD_EVENT: &str = "event";
/// The structured-log field name for the redacted human message.
pub const LOG_FIELD_MESSAGE: &str = "msg";

/// The severity of one [`LogRecord`].
#[derive(Clone, Copy, Debug, Eq, Ord, PartialEq, PartialOrd)]
pub enum LogLevel {
    /// Normal lifecycle and progress detail. Sampled first above
    /// [`LOG_SAMPLE_ABOVE_LINES_PER_SEC_PER_HOST`].
    Info,
    /// Reduced health, saturation, or a refused operation. Never sampled
    /// before `Info`.
    Warn,
    /// A failed durability, storage, or replication contract. Never sampled.
    Error,
}

impl LogLevel {
    /// Returns the stable log-field value for this severity.
    #[must_use]
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::Info => "info",
            Self::Warn => "warn",
            Self::Error => "error",
        }
    }
}

/// Every admitted log severity in stable order.
pub const LOG_LEVELS: [LogLevel; 3] = [LogLevel::Info, LogLevel::Warn, LogLevel::Error];

/// A bounded log event shared by every engine subsystem (D-72).
///
/// The vocabulary is deliberately lifecycle-shaped so storage, replication,
/// backup, restore, and usage export all report the same eight events and
/// no subsystem invents a per-database, per-tenant, or per-view event name.
/// Anything finer-grained belongs in a metric label from the closed registry
/// or in the redacted `msg` field, never in a new event.
#[derive(Clone, Copy, Debug, Eq, Ord, PartialEq, PartialOrd)]
pub enum LogEvent {
    /// The subsystem started serving.
    Started,
    /// The subsystem stopped serving.
    Stopped,
    /// The subsystem serves with reduced health or redundancy.
    Degraded,
    /// The subsystem returned to healthy from degraded or unhealthy.
    Recovered,
    /// A bounded queue or budget above its saturation threshold.
    Saturated,
    /// An operation refused before it began (admission, clock, validation).
    Refused,
    /// A begun operation failed against its contract.
    Failed,
    /// Long-running work (backup, restore, export) advanced.
    Progress,
}

impl LogEvent {
    /// Returns the stable log-field value for this event.
    #[must_use]
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::Started => "started",
            Self::Stopped => "stopped",
            Self::Degraded => "degraded",
            Self::Recovered => "recovered",
            Self::Saturated => "saturated",
            Self::Refused => "refused",
            Self::Failed => "failed",
            Self::Progress => "progress",
        }
    }
}

/// Every admitted log event in stable order.
pub const LOG_EVENTS: [LogEvent; 8] = [
    LogEvent::Started,
    LogEvent::Stopped,
    LogEvent::Degraded,
    LogEvent::Recovered,
    LogEvent::Saturated,
    LogEvent::Refused,
    LogEvent::Failed,
    LogEvent::Progress,
];

/// One structured log record (D-72).
///
/// Field names are the [`LOG_FIELD_*`] constants; level, component, and event
/// come from closed enums, so a tenant ID, database ID, view ID, query text,
/// path, or credential cannot become a field name. The message passes through
/// [`redact_diagnostic`] at construction, so secrets cannot reach the host
/// agent even when a dependency supplies them. Sampling above
/// [`LOG_SAMPLE_ABOVE_LINES_PER_SEC_PER_HOST`] lines/s per host and
/// [`LOG_RETENTION_DAYS`]-day retention are enforced by the
/// `ts-orchestrator` agent configuration, not by this value type.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct LogRecord {
    timestamp_millis: u64,
    level: LogLevel,
    component: Component,
    event: LogEvent,
    message: String,
}

impl LogRecord {
    /// Builds a record, redacting secrets from the message.
    #[must_use]
    pub fn new(
        timestamp_millis: u64,
        level: LogLevel,
        component: Component,
        event: LogEvent,
        message: &str,
    ) -> Self {
        let redacted = redact_diagnostic(message);
        let message = bounded_log_message(&redacted).to_owned();
        Self { timestamp_millis, level, component, event, message }
    }

    /// Returns the millisecond Unix timestamp.
    #[must_use]
    pub const fn timestamp_millis(&self) -> u64 {
        self.timestamp_millis
    }

    /// Returns the record severity.
    #[must_use]
    pub const fn level(&self) -> LogLevel {
        self.level
    }

    /// Returns the owning subsystem.
    #[must_use]
    pub const fn component(&self) -> Component {
        self.component
    }

    /// Returns the bounded event.
    #[must_use]
    pub const fn event(&self) -> LogEvent {
        self.event
    }

    /// Returns the redacted human message.
    #[must_use]
    pub fn message(&self) -> &str {
        &self.message
    }

    /// Renders one agent-shippable JSON log line with the [`LOG_FIELD_*`]
    /// names.
    ///
    /// Every value is encoded as JSON, including all C0 controls, DEL, and
    /// Unicode line separators. The result contains no literal line
    /// separator, so one call always produces exactly one log record.
    #[must_use]
    pub fn to_log_line(&self) -> String {
        let mut line = String::with_capacity(self.message.len().saturating_add(128));
        line.push('{');
        let valid = append_json_number_field(&mut line, LOG_FIELD_TIMESTAMP, self.timestamp_millis)
            && append_json_string_field(&mut line, LOG_FIELD_LEVEL, self.level.as_str())
            && append_json_string_field(&mut line, LOG_FIELD_COMPONENT, self.component.as_str())
            && append_json_string_field(&mut line, LOG_FIELD_EVENT, self.event.as_str())
            && append_json_string_field(&mut line, LOG_FIELD_MESSAGE, &self.message);
        if valid {
            line.push('}');
            line
        } else {
            // These keys and enum values are closed constants today. Keep a
            // fail-closed path so a future schema edit cannot produce an
            // unsafe key or leak a caller-supplied value.
            r#"{"error":"invalid log schema"}"#.to_owned()
        }
    }
}

#[derive(Default)]
struct SamplerState {
    second: Option<u64>,
    info_seen: u64,
    dropped_info: u64,
}

/// A deterministic per-host structured-log sampler.
///
/// At most [`LOG_SAMPLE_ABOVE_LINES_PER_SEC_PER_HOST`] Info records are
/// accepted in each one-second timestamp bucket. Warnings and errors always
/// pass through. The host agent remains responsible for the deployment-wide
/// rate and retention policy; this sampler protects in-process sinks and makes
/// the boundary testable without a wall-clock dependency.
pub struct LogSampler {
    state: Mutex<SamplerState>,
}

impl Default for LogSampler {
    fn default() -> Self {
        Self::new()
    }
}

impl LogSampler {
    /// Creates an empty sampler.
    #[must_use]
    pub fn new() -> Self {
        Self { state: Mutex::new(SamplerState::default()) }
    }

    /// Returns whether a record should be emitted. Timestamp buckets are
    /// derived from the record's trusted integer timestamp, so concurrent
    /// callers and test replay are deterministic.
    pub fn should_emit(&self, record: &LogRecord) -> bool {
        self.should_emit_at(record.timestamp_millis(), record.level())
    }

    /// Returns whether a level at a millisecond timestamp should be emitted.
    /// Non-Info records are never sampled.
    pub fn should_emit_at(&self, timestamp_millis: u64, level: LogLevel) -> bool {
        if level != LogLevel::Info {
            return true;
        }
        let second = timestamp_millis / 1_000;
        let mut state = lock(&self.state);
        if state.second != Some(second) {
            state.second = Some(second);
            state.info_seen = 0;
        }
        if state.info_seen < LOG_SAMPLE_ABOVE_LINES_PER_SEC_PER_HOST {
            state.info_seen += 1;
            true
        } else {
            state.dropped_info = state.dropped_info.saturating_add(1);
            false
        }
    }

    /// Returns the number of Info records dropped since construction.
    #[must_use]
    pub fn dropped_info(&self) -> u64 {
        lock(&self.state).dropped_info
    }

    /// Emits a record only when the sampler admits it.
    pub fn emit<S: LogSink>(&self, sink: &S, record: &LogRecord) -> bool {
        if self.should_emit(record) {
            sink.emit(record);
            true
        } else {
            false
        }
    }
}

fn bounded_log_message(input: &str) -> &str {
    if input.len() <= MAX_LOG_MESSAGE_BYTES {
        return input;
    }
    let mut end = MAX_LOG_MESSAGE_BYTES;
    while !input.is_char_boundary(end) {
        end -= 1;
    }
    &input[..end]
}

fn append_json_number_field(output: &mut String, key: &str, value: u64) -> bool {
    if !is_safe_log_field_name(key) {
        return false;
    }
    if output.len() > 1 {
        output.push(',');
    }
    output.push('"');
    output.push_str(key);
    output.push_str("\":");
    output.push_str(&value.to_string());
    true
}

fn append_json_string_field(output: &mut String, key: &str, value: &str) -> bool {
    let max_value_bytes =
        if key == LOG_FIELD_MESSAGE { MAX_LOG_MESSAGE_BYTES } else { MAX_LOG_FIELD_BYTES };
    if !is_safe_log_field_name(key) || value.len() > max_value_bytes {
        return false;
    }
    if output.len() > 1 {
        output.push(',');
    }
    output.push('"');
    output.push_str(key);
    output.push_str("\":");
    append_json_string(output, value);
    true
}

fn is_safe_log_field_name(key: &str) -> bool {
    !key.is_empty()
        && key.len() <= MAX_LOG_FIELD_BYTES
        && key
            .bytes()
            .all(|byte| byte.is_ascii_lowercase() || byte.is_ascii_digit() || byte == b'_')
}

fn append_json_string(output: &mut String, value: &str) {
    output.push('"');
    for character in value.chars() {
        match character {
            '"' => output.push_str("\\\""),
            '\\' => output.push_str("\\\\"),
            '\u{08}' => output.push_str("\\b"),
            '\t' => output.push_str("\\t"),
            '\n' => output.push_str("\\n"),
            '\u{0c}' => output.push_str("\\f"),
            '\r' => output.push_str("\\r"),
            '\u{00}'..='\u{1f}' | '\u{7f}' | '\u{85}' | '\u{2028}' | '\u{2029}' => {
                write!(output, "\\u{:04x}", character as u32).expect("writing String cannot fail");
            }
            _ => output.push(character),
        }
    }
    output.push('"');
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
        if let Some(prefix_end) = sensitive_colon_prefix_end(input, start, key_end) {
            key_end = prefix_end;
        }
        let key = &input[start..key_end];
        if let Some((value_start, value_end)) = secret_redaction_range(input, key_end, key) {
            output.push_str(&input[start..value_start]);
            output.push_str("[REDACTED]");
            scan = value_end;
            continue;
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

fn sensitive_colon_prefix_end(input: &str, start: usize, key_end: usize) -> Option<usize> {
    if input.as_bytes().get(key_end).is_some_and(|byte| *byte == b'=' || *byte == b':') {
        return None;
    }
    let relative = input[start..key_end].rfind(':')?;
    let prefix_end = start + relative;
    is_secret_key(&input[start..prefix_end]).then_some(prefix_end)
}

fn secret_redaction_range(input: &str, key_end: usize, key: &str) -> Option<(usize, usize)> {
    is_secret_key(key).then(|| secret_value_range(input, key_end, key)).flatten()
}

fn has_secret_suffix(key: &str, alias: &str) -> bool {
    let key = trim_unicode_tricks(key);
    if ascii_eq_ignore_case(key, alias) {
        return true;
    }
    let Some(offset) = key.len().checked_sub(alias.len()) else { return false };
    offset > 0
        && key.is_char_boundary(offset)
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

fn secret_value_range(input: &str, key_end: usize, key: &str) -> Option<(usize, usize)> {
    let mut cursor = key_end;
    // JSON-like and shell-like quoted keys carry a closing quote before the
    // assignment delimiter (`"password": ...`).
    if input[cursor..].chars().next().is_some_and(|character| character == '"' || character == '\'')
    {
        cursor += input[cursor..].chars().next().expect("checked above").len_utf8();
    }
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
        metrics.record_egress_bytes(Region::Unknown, EgressKind::Read, 1);
        let exposition = metrics.render_prometheus();
        assert!(exposition.contains("region=\"unknown\""));
        assert!(exposition.contains("operation=\"other\""));
        assert!(!exposition.contains("tenant"));
        assert!(!exposition.contains("document"));
        assert!(!exposition.contains("database"));
        assert!(!exposition.contains("collection"));
        assert!(!exposition.contains("view="));
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
        }
        for result in COMMIT_RESULTS {
            metrics.record_commit(result, Duration::MAX);
        }
        for mode in BACKUP_MODES {
            metrics.set_backup_last_success_age(mode, Duration::MAX);
            for outcome in TASK_OUTCOMES {
                metrics.record_backup(mode, outcome);
            }
        }
        metrics.set_clock_offset_seconds(f64::MAX);
        metrics.record_clock_write_refusal();
        for region in REGIONS {
            for kind in EGRESS_KINDS {
                metrics.record_egress_bytes(region, kind, u64::MAX);
            }
        }
        for component in COMPONENTS {
            metrics.set_component_health(component, HealthStatus::Unknown);
        }
        assert!(metrics.advance_usage_export_frontier(u64::MAX));
        metrics.record_usage_export_failure();
        let snapshot = metrics.snapshot();
        assert_eq!(snapshot.requests().len(), MAX_REQUEST_SERIES);
        assert_eq!(snapshot.egress_bytes().len(), MAX_EGRESS_SERIES);
        assert_eq!(snapshot.clock_offset_seconds(), Some(f64::MAX));
        assert_eq!(snapshot.clock_write_refusals(), 1);
        assert_eq!(snapshot.replication_lag().len(), MAX_REGION_LABEL_VALUES);
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
    fn clock_offset_and_egress_share_the_bounded_exposition() {
        let bare = MetricsRegistry::new().render_prometheus();
        assert!(
            bare.lines()
                .filter(|line| !line.starts_with('#'))
                .all(|line| !line.starts_with("terrabase_clock_offset_seconds")),
            "clock offset gauge must be absent until the collector publishes"
        );

        let metrics = MetricsRegistry::new();
        metrics.set_clock_offset_seconds(-0.012);
        metrics.set_clock_offset_seconds(f64::NAN);
        metrics.set_clock_offset_seconds(f64::INFINITY);
        metrics.record_clock_write_refusal();
        metrics.record_egress_bytes(Region::Gru, EgressKind::Replication, 1_500);
        metrics.record_egress_bytes(Region::Gru, EgressKind::Replication, 500);
        let snapshot = metrics.snapshot();
        assert_eq!(snapshot.clock_offset_seconds(), Some(-0.012));
        assert_eq!(snapshot.clock_write_refusals(), 1);
        assert_eq!(snapshot.egress_bytes(), &[(Region::Gru, EgressKind::Replication, 2_000)]);
        assert!(
            (CLOCK_OFFSET_WRITE_REFUSAL_THRESHOLD_SECONDS - 0.5).abs() < 1e-12,
            "D-69 refusal line changed; update the alert rules and runbook with it"
        );
        assert_eq!(
            EGRESS_MONTHLY_ALERT_BYTES, 8_000_000_000_000,
            "D-66 alert line changed; update the alert rules and runbook with it"
        );
        let exposition = snapshot.render_prometheus();
        assert!(exposition.contains("terrabase_clock_offset_seconds -0.012"));
        assert!(exposition.contains("terrabase_clock_write_refusals_total 1"));
        assert!(
            exposition
                .contains("terrabase_egress_bytes_total{region=\"GRU\",kind=\"replication\"} 2000")
        );
        metrics.clear_clock_offset_seconds();
        assert_eq!(metrics.snapshot().clock_offset_seconds(), None);
        assert!(!metrics.render_prometheus().contains("terrabase_clock_offset_seconds -0.012"));
    }

    #[test]
    fn log_records_use_closed_fields_and_redact_secrets() {
        let record = LogRecord::new(
            1_757_000_000_000,
            LogLevel::Error,
            Component::Storage,
            LogEvent::Failed,
            "fsync failed token=secret-value tenant=public",
        );
        assert!(!record.message().contains("secret-value"));
        assert!(record.message().contains("tenant=public"));
        assert_eq!(
            record.to_log_line(),
            r#"{"ts":1757000000000,"level":"error","component":"storage","event":"failed","msg":"fsync failed token=[REDACTED] tenant=public"}"#
        );
        assert_eq!(LOG_SAMPLE_ABOVE_LINES_PER_SEC_PER_HOST, 100);
        assert_eq!(LOG_RETENTION_DAYS, 14);
        assert_eq!(LOG_EVENTS.len(), 8);
        assert_eq!(MAX_EGRESS_SERIES, REGIONS.len() * EGRESS_KINDS.len());
        assert_eq!(MAX_CLOCK_SERIES, 2);
    }

    #[test]
    fn log_records_escape_line_breaks_without_splitting_records() {
        let record = LogRecord::new(
            1,
            LogLevel::Info,
            Component::Metrics,
            LogEvent::Progress,
            "first line\nsecond line\rfinal",
        );
        let line = record.to_log_line();
        assert!(!line.contains('\n'));
        assert!(!line.contains('\r'));
        assert!(line.ends_with(r#""msg":"first line\nsecond line\rfinal"}"#));
    }

    #[test]
    fn log_schema_rejects_unsafe_or_unbounded_field_names() {
        assert!(is_safe_log_field_name(LOG_FIELD_TIMESTAMP));
        assert!(is_safe_log_field_name(LOG_FIELD_LEVEL));
        assert!(is_safe_log_field_name(LOG_FIELD_COMPONENT));
        assert!(is_safe_log_field_name(LOG_FIELD_EVENT));
        assert!(is_safe_log_field_name(LOG_FIELD_MESSAGE));
        assert!(!is_safe_log_field_name(""));
        assert!(!is_safe_log_field_name("bad-key"));
        assert!(!is_safe_log_field_name("bad key"));
        assert!(!is_safe_log_field_name("bad\nkey"));
        assert!(!is_safe_log_field_name(&"x".repeat(MAX_LOG_FIELD_BYTES + 1)));
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

    #[test]
    fn subsystem_contract_is_closed_and_integrates_without_ids() {
        assert_eq!(SUBSYSTEMS.len(), 11);
        assert!(SUBSYSTEMS.iter().all(|subsystem| !subsystem.as_str().is_empty()));
        let metrics = MetricsRegistry::new();
        let hooks = ObservabilityHooks::new(&metrics);
        for subsystem in SUBSYSTEMS {
            hooks.operation(subsystem, Outcome::Success, Duration::from_millis(1));
        }
        hooks.guard(GuardKind::Rate);
        let snapshot = metrics.snapshot();
        assert_eq!(snapshot.subsystem_operations().len(), SUBSYSTEMS.len());
        assert_eq!(snapshot.guard_hits(), &[(GuardKind::Rate, 1)]);
        let exposition = snapshot.render_prometheus();
        assert!(exposition.contains("terrabase_subsystem_operations_total 11"));
        assert!(exposition.contains("terrabase_guard_hits_total 1"));
        for forbidden in [
            "database=",
            "database_id=",
            "tenant=",
            "tenant_id=",
            "view=",
            "view_id=",
            "document=",
            "document_id=",
            "query=",
            "query_id=",
            "request_id=",
        ] {
            assert!(!exposition.contains(forbidden), "forbidden label {forbidden}");
        }
    }

    #[test]
    fn textual_label_boundary_rejects_high_cardinality_and_unknown_values() {
        assert_eq!(
            BoundedLabelSet::try_from_pairs(&[("tenant_id", "customer-1")]),
            Err(LabelBoundaryError::HighCardinality)
        );
        assert_eq!(
            BoundedLabelSet::try_from_pairs(&[("view", "view-1")]),
            Err(LabelBoundaryError::HighCardinality)
        );
        assert_eq!(
            BoundedLabelSet::try_from_pairs(&[("query_text", "select 1")]),
            Err(LabelBoundaryError::Unsupported)
        );
        assert_eq!(
            BoundedLabelSet::try_from_pairs(&[("region", "sa-saopaulo-1")]),
            Err(LabelBoundaryError::InvalidValue)
        );
        assert_eq!(
            BoundedLabelSet::try_from_pairs(&[("region", "GRU"), ("subsystem", "sql")]),
            Ok(BoundedLabelSet::new(false, Some(Region::Gru), Some(Subsystem::Sql)))
        );
    }

    #[test]
    fn log_sampler_caps_info_per_second_and_preserves_failures() {
        let sampler = LogSampler::new();
        let info =
            LogRecord::new(2_000, LogLevel::Info, Component::Metrics, LogEvent::Progress, "ok");
        for _ in 0..LOG_SAMPLE_ABOVE_LINES_PER_SEC_PER_HOST {
            assert!(sampler.should_emit(&info));
        }
        assert!(!sampler.should_emit(&info));
        assert_eq!(sampler.dropped_info(), 1);
        assert!(sampler.should_emit_at(2_999, LogLevel::Error));
        assert!(sampler.should_emit_at(3_000, LogLevel::Info));
    }
}
