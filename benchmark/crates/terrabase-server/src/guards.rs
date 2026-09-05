//! Abuse guards (D-64) and per-tenant resource governance (E-10).
//!
//! Every limit in this module is the D-64 table, per database, per plan
//! (Free / Starter / Pro / Scale / custom, owner-tunable): read + query
//! requests/s `100 / 1_000 / 5_000 / custom`, direct member read bandwidth
//! `50 GB / 500 GB / 2 TB / custom` per month, distinct active shared
//! views `1k / 10k / 100k / custom`, WebSocket connections
//! `100 / 1_000 / 10_000 / custom`, and webhook endpoints `3 / 10 / 50 /
//! custom`. CDN-delivered reactive data has no bandwidth guard; subscribers
//! per view are unlimited on every plan.
//!
//! The design rules are the goal's: exceeding a guard returns a typed error
//! with the fix and nothing is billed; metrics stay cheap (no per-database,
//! per-tenant, or per-view label — D-72), so the only shared structure is
//! this in-memory, per-database-sharded limiter whose rejections the server
//! reports through the existing bounded admission-rejection family.
//! Isolation between tenants is this module's job, not the kernel's (D-69):
//! every tenant gets its own shard, so an adversarial neighbour can spend
//! exactly its own budget and never its victim's. The cgroup rendering
//! ([`TenantBudget::render_systemd_slice`]) is the typed input the
//! orchestrator's host agent turns into `CPUWeight` / `MemoryMax` /
//! `IOWeight`; enforcement of the request budgets lives here.

use std::collections::{BTreeSet, HashMap};
use std::sync::{Arc, Mutex, PoisonError, RwLock};
use std::time::{Duration, Instant};

/// Bytes in one decimal gigabyte, the unit the D-64 bandwidth cells use.
const GB: u64 = 1_000_000_000;
/// Bytes in one decimal terabyte, the unit the Pro bandwidth cell uses.
const TB: u64 = 1_000 * GB;
/// Bytes in one binary gibibyte, retained for cgroup memory bounds only.
const GIB: u64 = 1024 * 1024 * 1024;
/// The fixed rate window every read/query budget is accounted in.
const RATE_WINDOW: Duration = Duration::from_secs(1);
/// The longest guard identity (view name, endpoint) tracked per database.
/// Longer identities fail closed instead of growing the resident sets.
const MAX_GUARD_ID_BYTES: usize = 256;
/// The smallest cgroup v2 weight (`CPUWeight`, `IOWeight`) this module emits.
const MIN_WEIGHT: u32 = 1;
/// The largest cgroup v2 weight (`CPUWeight`, `IOWeight`) this module emits.
const MAX_WEIGHT: u32 = 10_000;
/// The smallest per-tenant memory budget this module emits (4 MiB).
const MIN_MEMORY_BYTES: u64 = 4 * 1024 * 1024;
/// The largest per-tenant memory budget this module emits (1 TiB).
const MAX_MEMORY_BYTES: u64 = 1024 * GIB;

/// One database's five D-64 abuse budgets. Every field is a hard ceiling;
/// a zero field denies that dimension outright (fail-closed custom plans).
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct AbuseLimits {
    /// Admitted read + query requests per second.
    pub read_query_rps: u64,
    /// Admitted direct member read bytes per calendar month. CDN-delivered
    /// reactive bytes never enter this counter.
    pub direct_read_bandwidth_bytes_per_month: u64,
    /// Admitted distinct active shared views.
    pub distinct_views: u64,
    /// Admitted concurrent WebSocket connections.
    pub ws_connections: u64,
    /// Admitted webhook endpoints.
    pub webhook_endpoints: u64,
}

impl AbuseLimits {
    /// A fail-closed set of cells used until a Scale owner supplies its
    /// negotiated limits.
    pub const ZERO: Self = Self {
        read_query_rps: 0,
        direct_read_bandwidth_bytes_per_month: 0,
        distinct_views: 0,
        ws_connections: 0,
        webhook_endpoints: 0,
    };
}

/// The billed plan selecting one row of the D-64 table.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum Plan {
    /// `100 req/s, 50 GB, 1k views, 100 WS, 3 webhooks`.
    Free,
    /// `1_000 req/s, 500 GB, 10k views, 1_000 WS, 10 webhooks`.
    Starter,
    /// `5_000 req/s, 2 TB, 100k views, 10_000 WS, 50 webhooks`.
    Pro,
    /// Owner-tunable limits for negotiated databases. A Scale database must
    /// receive an explicit override through [`AbuseGuards::set_limits`]; an
    /// unconfigured Scale database fails closed (all five limits are zero).
    Scale,
    /// Fully owner-tunable budgets; a zero cell denies that dimension.
    Custom(AbuseLimits),
}

impl Plan {
    /// Returns the plan's short name as the billing lane spells it.
    #[must_use]
    pub const fn name(self) -> &'static str {
        match self {
            Self::Free => "free",
            Self::Starter => "starter",
            Self::Pro => "pro",
            Self::Scale => "scale",
            Self::Custom(_) => "custom",
        }
    }

    /// Returns the five D-64 budgets for this plan.
    #[must_use]
    pub const fn limits(self) -> AbuseLimits {
        match self {
            Self::Free => AbuseLimits {
                read_query_rps: 100,
                direct_read_bandwidth_bytes_per_month: 50 * GB,
                distinct_views: 1_000,
                ws_connections: 100,
                webhook_endpoints: 3,
            },
            Self::Starter => AbuseLimits {
                read_query_rps: 1_000,
                direct_read_bandwidth_bytes_per_month: 500 * GB,
                distinct_views: 10_000,
                ws_connections: 1_000,
                webhook_endpoints: 10,
            },
            Self::Pro => AbuseLimits {
                read_query_rps: 5_000,
                direct_read_bandwidth_bytes_per_month: 2 * TB,
                distinct_views: 100_000,
                ws_connections: 10_000,
                webhook_endpoints: 50,
            },
            Self::Scale => AbuseLimits::ZERO,
            Self::Custom(limits) => limits,
        }
    }

    /// Parses the billing lane's plan name; unknown names fail closed to
    /// `None` so the caller keeps the current (Free by default) plan.
    #[must_use]
    pub fn from_name(name: &str) -> Option<Self> {
        match name {
            "free" => Some(Self::Free),
            "starter" => Some(Self::Starter),
            "pro" => Some(Self::Pro),
            "scale" => Some(Self::Scale),
            _ => None,
        }
    }
}

/// Which D-64 budget a rejection spent.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum GuardKind {
    /// The read + query requests/s budget.
    ReadQueryRate,
    /// The monthly direct member read byte budget.
    DirectReadBandwidth,
    /// The distinct active shared view budget.
    DistinctViews,
    /// The concurrent WebSocket connection budget.
    WsConnections,
    /// The webhook endpoint budget.
    WebhookEndpoints,
}

impl GuardKind {
    /// Returns the stable short name carried by operator-facing text.
    #[must_use]
    pub const fn as_str(self) -> &'static str {
        match self {
            Self::ReadQueryRate => "read_query_rate",
            Self::DirectReadBandwidth => "direct_read_bandwidth",
            Self::DistinctViews => "distinct_views",
            Self::WsConnections => "ws_connections",
            Self::WebhookEndpoints => "webhook_endpoints",
        }
    }

    /// Returns the one-line remedy for a spent budget of this kind: what
    /// to change on the client, and which D-64 row raises the ceiling.
    /// Nothing here names a price; over-limit calls are never billed.
    #[must_use]
    pub fn fix_for(self, limit: u64) -> String {
        match self {
            Self::ReadQueryRate => format!(
                "slow to under {limit} read+query req/s per database (retry after 1 s), \
                 or move the database to a plan with a higher D-64 guard \
                 (Free 100 / Starter 1000 / Pro 5000 / custom)"
            ),
            Self::DirectReadBandwidth => format!(
                "direct member reads passed the {} monthly budget; serve repeat reads \
                 from the CDN (sync.api.*) or raise the D-64 bandwidth guard \
                 (Free 50 GB / Starter 500 GB / Pro 2 TB / custom)",
                bytes_text(limit)
            ),
            Self::DistinctViews => format!(
                "the database already holds its {limit} distinct active shared views; \
                 close an unused view or raise the D-64 view guard \
                 (Free 1000 / Starter 10000 / Pro 100000 / custom)"
            ),
            Self::WsConnections => format!(
                "the database already holds its {limit} WebSocket connections; close \
                 an idle socket (polling needs no connection) or raise the D-64 \
                 connection guard (Free 100 / Starter 1000 / Pro 10000 / custom)"
            ),
            Self::WebhookEndpoints => format!(
                "the database already holds its {limit} webhook endpoints; delete an \
                 endpoint or raise the D-64 webhook guard \
                 (Free 3 / Starter 10 / Pro 50 / custom)"
            ),
        }
    }
}

/// Renders a bandwidth cell the way the fix text spells it.
fn bytes_text(bytes: u64) -> String {
    if bytes == 50 * GB {
        "50 GB".to_owned()
    } else if bytes == 500 * GB {
        "500 GB".to_owned()
    } else if bytes == 2 * TB {
        "2 TB".to_owned()
    } else {
        format!("{bytes} bytes")
    }
}

/// One spent D-64 budget: the typed error the API answers 429 with.
/// The `code` is always `abuse_guard_exceeded`; `message` names the
/// budget, the observed use, and the context; `fix` names the remedy.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct GuardRejection {
    kind: GuardKind,
    limit: u64,
    observed: u64,
    detail: String,
}

impl GuardRejection {
    /// Records one refusal. Private: only [`AbuseGuards`] mints refusals,
    /// so a rejection always names a real budget.
    fn new(kind: GuardKind, limit: u64, observed: u64, detail: &str) -> Self {
        Self { kind, limit, observed, detail: detail.to_owned() }
    }

    /// Returns which D-64 budget refused.
    #[must_use]
    pub const fn kind(&self) -> GuardKind {
        self.kind
    }

    /// Returns the budget ceiling that refused.
    #[must_use]
    pub const fn limit(&self) -> u64 {
        self.limit
    }

    /// Returns the observed use that crossed the ceiling.
    #[must_use]
    pub const fn observed(&self) -> u64 {
        self.observed
    }

    /// Returns the refusal's machine-safe context (window, counts).
    #[must_use]
    pub fn detail(&self) -> &str {
        &self.detail
    }

    /// Returns the stable error code the envelope carries.
    #[must_use]
    pub const fn code(&self) -> &'static str {
        "abuse_guard_exceeded"
    }

    /// Returns the human message the envelope carries.
    #[must_use]
    pub fn message(&self) -> String {
        let (limit, observed, detail) = (self.limit, self.observed, self.detail.as_str());
        match self.kind {
            GuardKind::ReadQueryRate => format!(
                "read+query rate {observed}/{limit} req/s exceeded on this database ({detail})"
            ),
            GuardKind::DirectReadBandwidth => format!(
                "direct member read bandwidth {observed}/{limit} exceeded this month ({detail})"
            ),
            GuardKind::DistinctViews => {
                format!(
                    "distinct shared views {observed}/{limit} exceeded on this database ({detail})"
                )
            }
            GuardKind::WsConnections => {
                format!(
                    "WebSocket connections {observed}/{limit} exceeded on this database ({detail})"
                )
            }
            GuardKind::WebhookEndpoints => {
                format!("webhook endpoints {observed}/{limit} exceeded on this database ({detail})")
            }
        }
    }

    /// Returns the one-line remedy the envelope carries.
    #[must_use]
    pub fn fix(&self) -> String {
        self.kind.fix_for(self.limit)
    }
}

impl std::fmt::Display for GuardRejection {
    fn fmt(&self, formatter: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        formatter.write_str(&self.message())
    }
}

impl std::error::Error for GuardRejection {}

/// One tenant's governance budget: the cgroup v2 shares the orchestrator's
/// host agent enforces around the shared member process (D-69), plus the
/// in-process admission this module enforces. Weights follow the cgroup v2
/// `1..=10_000` range (`100` is the kernel default); memory is bytes.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct TenantBudget {
    cpu_weight: u32,
    memory_max_bytes: u64,
    io_weight: u32,
}

impl TenantBudget {
    /// Returns the governance budget for a plan. Custom plans carry the
    /// Scale shares until [`AbuseGuards::set_tenant_budget`] overrides them.
    #[must_use]
    pub const fn for_plan(plan: Plan) -> Self {
        match plan {
            Plan::Free => {
                Self { cpu_weight: 25, memory_max_bytes: 256 * 1024 * 1024, io_weight: 25 }
            }
            Plan::Starter => {
                Self { cpu_weight: 100, memory_max_bytes: 1024 * 1024 * 1024, io_weight: 100 }
            }
            Plan::Pro => {
                Self { cpu_weight: 400, memory_max_bytes: 4 * 1024 * 1024 * 1024, io_weight: 400 }
            }
            Plan::Scale | Plan::Custom(_) => {
                Self { cpu_weight: 2000, memory_max_bytes: 8 * 1024 * 1024 * 1024, io_weight: 2000 }
            }
        }
    }

    /// Builds an owner-tuned budget. Returns `None` (fail-closed, the
    /// caller keeps the current budget) when a weight leaves `1..=10_000`
    /// or memory leaves `4 MiB..=1 TiB`.
    #[must_use]
    pub const fn custom(cpu_weight: u32, memory_max_bytes: u64, io_weight: u32) -> Option<Self> {
        if cpu_weight < MIN_WEIGHT
            || cpu_weight > MAX_WEIGHT
            || io_weight < MIN_WEIGHT
            || io_weight > MAX_WEIGHT
            || memory_max_bytes < MIN_MEMORY_BYTES
            || memory_max_bytes > MAX_MEMORY_BYTES
        {
            return None;
        }
        Some(Self { cpu_weight, memory_max_bytes, io_weight })
    }

    /// Returns the cgroup v2 `CPUWeight` share.
    #[must_use]
    pub const fn cpu_weight(self) -> u32 {
        self.cpu_weight
    }

    /// Returns the cgroup v2 `MemoryMax` in bytes.
    #[must_use]
    pub const fn memory_max_bytes(self) -> u64 {
        self.memory_max_bytes
    }

    /// Returns the cgroup v2 `IOWeight` share.
    #[must_use]
    pub const fn io_weight(self) -> u32 {
        self.io_weight
    }

    /// Admits one memory request against this tenant's cap.
    #[must_use]
    pub const fn admits_memory(self, request_bytes: u64) -> bool {
        request_bytes <= self.memory_max_bytes
    }

    /// Computes this tenant's fair CPU share of `total_milli_cpus` when the
    /// contending weights on the host sum to `total_weight`. A zero weight
    /// sum admits nothing rather than dividing by zero.
    #[must_use]
    pub fn cpu_share_milli(self, total_milli_cpus: u64, total_weight: u64) -> u64 {
        if total_weight == 0 {
            return 0;
        }
        u64::from(self.cpu_weight).saturating_mul(total_milli_cpus) / total_weight
    }

    /// Renders the systemd slice drop-in the orchestrator's host agent
    /// applies for this tenant (`CPUWeight` / `MemoryMax` / `IOWeight`,
    /// D-69). The tenant name is sanitized to one safe comment token, so
    /// untrusted scope text can never break the unit directives.
    #[must_use]
    pub fn render_systemd_slice(self, tenant: &str) -> String {
        format!(
            "# Tenant slice for '{}': generated by terrabase-server abuse guards \
             (D-64/D-69); do not hand-edit.\n[Slice]\nCPUWeight={}\nMemoryMax={}\nIOWeight={}\n",
            sanitize_tenant(tenant),
            self.cpu_weight,
            self.memory_max_bytes,
            self.io_weight
        )
    }
}

/// Restricts a tenant name to one safe comment token.
fn sanitize_tenant(tenant: &str) -> String {
    let mut out = String::new();
    for character in tenant.chars().filter(|character| !character.is_control()).take(64) {
        out.push(if character.is_ascii_alphanumeric() || matches!(character, '-' | '_' | '.') {
            character
        } else {
            '_'
        });
    }
    if out.is_empty() {
        out.push_str("tenant");
    }
    out
}

/// Converts a resident-set length without a truncating cast.
fn len64(len: usize) -> u64 {
    u64::try_from(len).unwrap_or(u64::MAX)
}

/// One database's live guard counters. Sharded per database behind its own
/// lock: an adversarial neighbour hammering its shard never blocks its
/// victim's shard past one map lookup.
struct DatabaseGuardState {
    window_start: Option<Instant>,
    window_count: u64,
    bandwidth_bytes: u64,
    views: BTreeSet<String>,
    ws_current: u64,
    webhooks: BTreeSet<String>,
}

/// The D-64 enforcement point: per-database plans, budgets, and counters.
///
/// Unknown databases run the Free row (fail-closed): a database the billing
/// lane never named is throttled like Free, never unthrottled. The monthly
/// bandwidth counter resets through [`Self::reset_bandwidth`] (the
/// orchestrator's monthly rotation); the rate window rolls every second on
/// its own.
pub struct AbuseGuards {
    plans: RwLock<HashMap<String, Plan>>,
    /// Explicit owner-tuned limits win over the plan's built-in row. Keeping
    /// overrides separate means a plan change never silently discards an
    /// operator's negotiated guard values.
    limit_overrides: RwLock<HashMap<String, AbuseLimits>>,
    states: RwLock<HashMap<String, Arc<Mutex<DatabaseGuardState>>>>,
    budgets: Mutex<HashMap<String, TenantBudget>>,
}

impl AbuseGuards {
    /// Creates an empty guard set: every database runs Free until
    /// [`Self::set_plan`] names its plan.
    #[must_use]
    pub fn new() -> Self {
        Self {
            plans: RwLock::new(HashMap::new()),
            limit_overrides: RwLock::new(HashMap::new()),
            states: RwLock::new(HashMap::new()),
            budgets: Mutex::new(HashMap::new()),
        }
    }

    /// Names one database's plan (the billing lane's input).
    pub fn set_plan(&self, database: &str, plan: Plan) {
        if database.is_empty() {
            return;
        }
        self.plans
            .write()
            .unwrap_or_else(PoisonError::into_inner)
            .insert(database.to_owned(), plan);
    }

    /// Returns one database's plan, Free for databases nobody named.
    #[must_use]
    pub fn plan_of(&self, database: &str) -> Plan {
        self.plans
            .read()
            .unwrap_or_else(PoisonError::into_inner)
            .get(database)
            .copied()
            .unwrap_or(Plan::Free)
    }

    /// Sets the five owner-tunable D-64 cells for one database. Overrides
    /// apply to Free, Starter, Pro, and Scale alike and remain in force until
    /// [`Self::clear_limits`] is called. Zero is a deliberate fail-closed
    /// deny-all value, useful while a database is being provisioned.
    pub fn set_limits(&self, database: &str, limits: AbuseLimits) {
        if database.is_empty() {
            return;
        }
        self.limit_overrides
            .write()
            .unwrap_or_else(PoisonError::into_inner)
            .insert(database.to_owned(), limits);
    }

    /// Alias for callers that receive a limits update from the control plane.
    pub fn set_limits_override(&self, database: &str, limits: AbuseLimits) {
        self.set_limits(database, limits);
    }

    /// Removes the owner-tuned cells and resumes the selected plan row.
    pub fn clear_limits(&self, database: &str) {
        self.limit_overrides.write().unwrap_or_else(PoisonError::into_inner).remove(database);
    }

    /// Returns the effective D-64 cells for one database. Unknown databases
    /// use Free; Scale without an override is zero and therefore fails closed.
    #[must_use]
    pub fn limits_of(&self, database: &str) -> AbuseLimits {
        self.limit_overrides
            .read()
            .unwrap_or_else(PoisonError::into_inner)
            .get(database)
            .copied()
            .unwrap_or_else(|| self.plan_of(database).limits())
    }

    /// Alias used by admission callers that prefer the plan terminology.
    #[must_use]
    pub fn effective_limits(&self, database: &str) -> AbuseLimits {
        self.limits_of(database)
    }

    /// Returns (cloning) one database's counter shard, creating it on first
    /// use. The map lock is held for the lookup only.
    fn state_for(&self, database: &str) -> Arc<Mutex<DatabaseGuardState>> {
        if let Some(state) =
            self.states.read().unwrap_or_else(PoisonError::into_inner).get(database)
        {
            return Arc::clone(state);
        }
        let mut states = self.states.write().unwrap_or_else(PoisonError::into_inner);
        states
            .entry(database.to_owned())
            .or_insert_with(|| {
                Arc::new(Mutex::new(DatabaseGuardState {
                    window_start: None,
                    window_count: 0,
                    bandwidth_bytes: 0,
                    views: BTreeSet::new(),
                    ws_current: 0,
                    webhooks: BTreeSet::new(),
                }))
            })
            .clone()
    }

    /// Admits one read or query against the database's per-second budget.
    ///
    /// # Errors
    ///
    /// Returns the typed [`GuardRejection`] when the 1 s window is spent,
    /// the custom budget is zero, or the scope is empty.
    pub fn check_read_query(&self, database: &str) -> Result<(), GuardRejection> {
        if database.is_empty() {
            return Err(GuardRejection::new(
                GuardKind::ReadQueryRate,
                0,
                0,
                "empty database scope",
            ));
        }
        let limit = self.limits_of(database).read_query_rps;
        if limit == 0 {
            return Err(GuardRejection::new(
                GuardKind::ReadQueryRate,
                0,
                1,
                "the custom guard is zero: deny-all",
            ));
        }
        let slot = self.state_for(database);
        let mut state = slot.lock().unwrap_or_else(PoisonError::into_inner);
        let now = Instant::now();
        let window_spent =
            state.window_start.is_some_and(|start| now.duration_since(start) < RATE_WINDOW);
        if window_spent {
            if state.window_count >= limit {
                return Err(GuardRejection::new(
                    GuardKind::ReadQueryRate,
                    limit,
                    state.window_count.saturating_add(1),
                    "1 s window is spent",
                ));
            }
            state.window_count = state.window_count.saturating_add(1);
        } else {
            state.window_start = Some(now);
            state.window_count = 1;
        }
        Ok(())
    }

    /// Accounts served direct-read bytes against the monthly budget. The
    /// byte that would cross the ceiling is refused and recorded nothing;
    /// earlier bytes stay admitted.
    ///
    /// # Errors
    ///
    /// Returns the typed [`GuardRejection`] when the month is spent or the
    /// scope is empty.
    pub fn record_direct_read_bytes(
        &self,
        database: &str,
        bytes: u64,
    ) -> Result<(), GuardRejection> {
        if database.is_empty() {
            return Err(GuardRejection::new(
                GuardKind::DirectReadBandwidth,
                0,
                bytes,
                "empty database scope",
            ));
        }
        let limit = self.limits_of(database).direct_read_bandwidth_bytes_per_month;
        if limit == 0 {
            return Err(GuardRejection::new(
                GuardKind::DirectReadBandwidth,
                0,
                bytes,
                "the custom guard is zero: deny-all",
            ));
        }
        let slot = self.state_for(database);
        let mut state = slot.lock().unwrap_or_else(PoisonError::into_inner);
        // Check the subtraction first. Saturating addition alone would make
        // a `u64::MAX` limit admit bytes after the counter had overflowed.
        let remaining = limit.saturating_sub(state.bandwidth_bytes);
        if bytes > remaining {
            let observed = state.bandwidth_bytes.saturating_add(bytes);
            return Err(GuardRejection::new(
                GuardKind::DirectReadBandwidth,
                limit,
                observed,
                "monthly direct-read budget is spent",
            ));
        }
        state.bandwidth_bytes += bytes;
        Ok(())
    }

    /// Returns one database's accounted direct-read bytes this month.
    #[must_use]
    pub fn bandwidth_used(&self, database: &str) -> u64 {
        self.states
            .read()
            .unwrap_or_else(PoisonError::into_inner)
            .get(database)
            .map_or(0, |slot| slot.lock().unwrap_or_else(PoisonError::into_inner).bandwidth_bytes)
    }

    /// Rotates one database's monthly bandwidth counter (the orchestrator's
    /// monthly rotation calls this; tests call it for determinism).
    pub fn reset_bandwidth(&self, database: &str) {
        if let Some(slot) =
            self.states.read().unwrap_or_else(PoisonError::into_inner).get(database).cloned()
        {
            slot.lock().unwrap_or_else(PoisonError::into_inner).bandwidth_bytes = 0;
        }
    }

    /// Attaches one distinct shared view identity against the view budget.
    /// Re-attaching an already active identity is idempotent and free.
    ///
    /// # Errors
    ///
    /// Returns the typed [`GuardRejection`] when the budget is spent, the
    /// identity exceeds 256 bytes, or a scope is empty.
    pub fn attach_view(&self, database: &str, view: &str) -> Result<(), GuardRejection> {
        if database.is_empty() || view.is_empty() {
            return Err(GuardRejection::new(
                GuardKind::DistinctViews,
                0,
                0,
                "empty database or view scope",
            ));
        }
        let limit = self.limits_of(database).distinct_views;
        if view.len() > MAX_GUARD_ID_BYTES {
            return Err(GuardRejection::new(
                GuardKind::DistinctViews,
                limit,
                len64(view.len()),
                "view identity exceeds 256 bytes",
            ));
        }
        let slot = self.state_for(database);
        let mut state = slot.lock().unwrap_or_else(PoisonError::into_inner);
        if !state.views.contains(view) {
            let bound = usize::try_from(limit).unwrap_or(usize::MAX);
            if state.views.len() >= bound {
                return Err(GuardRejection::new(
                    GuardKind::DistinctViews,
                    limit,
                    len64(state.views.len()).saturating_add(1),
                    "distinct-view budget is spent",
                ));
            }
            state.views.insert(view.to_owned());
        }
        Ok(())
    }

    /// Detaches one view identity; unknown identities are a no-op.
    pub fn detach_view(&self, database: &str, view: &str) {
        if let Some(slot) =
            self.states.read().unwrap_or_else(PoisonError::into_inner).get(database).cloned()
        {
            slot.lock().unwrap_or_else(PoisonError::into_inner).views.remove(view);
        }
    }

    /// Returns one database's active distinct view count.
    #[must_use]
    pub fn view_count(&self, database: &str) -> u64 {
        self.states.read().unwrap_or_else(PoisonError::into_inner).get(database).map_or(0, |slot| {
            len64(slot.lock().unwrap_or_else(PoisonError::into_inner).views.len())
        })
    }

    /// Admits one WebSocket connection against the connection budget.
    ///
    /// # Errors
    ///
    /// Returns the typed [`GuardRejection`] when the budget is spent or the
    /// scope is empty. Every admitted slot must be released with
    /// [`Self::release_ws`]; the socket task's drop guard owns that call.
    pub fn acquire_ws(&self, database: &str) -> Result<(), GuardRejection> {
        if database.is_empty() {
            return Err(GuardRejection::new(
                GuardKind::WsConnections,
                0,
                0,
                "empty database scope",
            ));
        }
        let limit = self.limits_of(database).ws_connections;
        let slot = self.state_for(database);
        let mut state = slot.lock().unwrap_or_else(PoisonError::into_inner);
        if state.ws_current >= limit {
            return Err(GuardRejection::new(
                GuardKind::WsConnections,
                limit,
                state.ws_current.saturating_add(1),
                "connection budget is spent",
            ));
        }
        state.ws_current = state.ws_current.saturating_add(1);
        Ok(())
    }

    /// Releases one WebSocket slot; releasing an empty database is a no-op.
    pub fn release_ws(&self, database: &str) {
        if let Some(slot) =
            self.states.read().unwrap_or_else(PoisonError::into_inner).get(database).cloned()
        {
            let mut state = slot.lock().unwrap_or_else(PoisonError::into_inner);
            state.ws_current = state.ws_current.saturating_sub(1);
        }
    }

    /// Returns one database's held WebSocket slot count.
    #[must_use]
    pub fn ws_count(&self, database: &str) -> u64 {
        self.states
            .read()
            .unwrap_or_else(PoisonError::into_inner)
            .get(database)
            .map_or(0, |slot| slot.lock().unwrap_or_else(PoisonError::into_inner).ws_current)
    }

    /// Registers one webhook endpoint identity against the endpoint budget.
    /// Re-registering a known endpoint is idempotent and free.
    ///
    /// # Errors
    ///
    /// Returns the typed [`GuardRejection`] when the budget is spent, the
    /// identity exceeds 256 bytes, or a scope is empty.
    pub fn register_webhook(&self, database: &str, endpoint: &str) -> Result<(), GuardRejection> {
        if database.is_empty() || endpoint.is_empty() {
            return Err(GuardRejection::new(
                GuardKind::WebhookEndpoints,
                0,
                0,
                "empty database or endpoint scope",
            ));
        }
        let limit = self.limits_of(database).webhook_endpoints;
        if endpoint.len() > MAX_GUARD_ID_BYTES {
            return Err(GuardRejection::new(
                GuardKind::WebhookEndpoints,
                limit,
                len64(endpoint.len()),
                "endpoint identity exceeds 256 bytes",
            ));
        }
        let slot = self.state_for(database);
        let mut state = slot.lock().unwrap_or_else(PoisonError::into_inner);
        if !state.webhooks.contains(endpoint) {
            let bound = usize::try_from(limit).unwrap_or(usize::MAX);
            if state.webhooks.len() >= bound {
                return Err(GuardRejection::new(
                    GuardKind::WebhookEndpoints,
                    limit,
                    len64(state.webhooks.len()).saturating_add(1),
                    "webhook endpoint budget is spent",
                ));
            }
            state.webhooks.insert(endpoint.to_owned());
        }
        Ok(())
    }

    /// Unregisters one endpoint; unknown endpoints are a no-op.
    pub fn unregister_webhook(&self, database: &str, endpoint: &str) {
        if let Some(slot) =
            self.states.read().unwrap_or_else(PoisonError::into_inner).get(database).cloned()
        {
            slot.lock().unwrap_or_else(PoisonError::into_inner).webhooks.remove(endpoint);
        }
    }

    /// Returns one database's registered webhook endpoint count.
    #[must_use]
    pub fn webhook_count(&self, database: &str) -> u64 {
        self.states.read().unwrap_or_else(PoisonError::into_inner).get(database).map_or(0, |slot| {
            len64(slot.lock().unwrap_or_else(PoisonError::into_inner).webhooks.len())
        })
    }

    /// Names one tenant's governance budget (the orchestrator's input).
    pub fn set_tenant_budget(&self, tenant: &str, budget: TenantBudget) {
        self.budgets
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
            .insert(tenant.to_owned(), budget);
    }

    /// Returns one tenant's governance budget, Free shares for tenants
    /// nobody named (fail-closed: unnamed tenants get the floor, never the
    /// ceiling).
    #[must_use]
    pub fn tenant_budget(&self, tenant: &str) -> TenantBudget {
        self.budgets
            .lock()
            .unwrap_or_else(PoisonError::into_inner)
            .get(tenant)
            .copied()
            .unwrap_or_else(|| TenantBudget::for_plan(Plan::Free))
    }

    /// Clears one database's windows and counters (rate window, bandwidth,
    /// views, connections, endpoints) while keeping its plan. Tests and the
    /// monthly rotation use this; production traffic never calls it.
    pub fn reset_database(&self, database: &str) {
        if let Some(slot) =
            self.states.read().unwrap_or_else(PoisonError::into_inner).get(database).cloned()
        {
            let mut state = slot.lock().unwrap_or_else(PoisonError::into_inner);
            state.window_start = None;
            state.window_count = 0;
            state.bandwidth_bytes = 0;
            state.views.clear();
            state.ws_current = 0;
            state.webhooks.clear();
        }
    }
}

impl Default for AbuseGuards {
    fn default() -> Self {
        Self::new()
    }
}

/// The adversarial-neighbour acceptance check (E-10): the victim's p99
/// under a flooding neighbour stays within twice its solo p99. A zero solo
/// baseline passes only a zero victim observation.
#[must_use]
pub fn victim_p99_within_2x_solo(solo_p99: Duration, victim_p99: Duration) -> bool {
    match solo_p99.checked_mul(2) {
        Some(bound) => victim_p99 <= bound,
        None => true,
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::Arc;
    use std::sync::atomic::{AtomicBool, Ordering};

    /// The p99 of a latency sample: the 99th percentile element.
    fn p99(mut samples: Vec<Duration>) -> Duration {
        samples.sort_unstable();
        let index = samples.len().saturating_mul(99).div_ceil(100).saturating_sub(1);
        samples[index]
    }

    #[test]
    fn plan_defaults_match_the_d64_table() {
        assert_eq!(
            Plan::Free.limits(),
            AbuseLimits {
                read_query_rps: 100,
                direct_read_bandwidth_bytes_per_month: 50 * GB,
                distinct_views: 1_000,
                ws_connections: 100,
                webhook_endpoints: 3,
            }
        );
        assert_eq!(Plan::Starter.limits().read_query_rps, 1_000);
        assert_eq!(Plan::Starter.limits().direct_read_bandwidth_bytes_per_month, 500 * GB);
        assert_eq!(Plan::Starter.limits().distinct_views, 10_000);
        assert_eq!(Plan::Starter.limits().ws_connections, 1_000);
        assert_eq!(Plan::Starter.limits().webhook_endpoints, 10);
        assert_eq!(Plan::Pro.limits().read_query_rps, 5_000);
        assert_eq!(Plan::Pro.limits().direct_read_bandwidth_bytes_per_month, 2 * TB);
        assert_eq!(Plan::Pro.limits().distinct_views, 100_000);
        assert_eq!(Plan::Pro.limits().ws_connections, 10_000);
        assert_eq!(Plan::Pro.limits().webhook_endpoints, 50);
        assert_eq!(Plan::from_name("pro"), Some(Plan::Pro));
        assert_eq!(Plan::from_name("penthouse"), None);
    }

    #[test]
    fn custom_plans_are_owner_tunable_and_zero_denies() {
        let guards = AbuseGuards::new();
        guards.set_plan(
            "custom-db",
            Plan::Custom(AbuseLimits {
                read_query_rps: 7,
                direct_read_bandwidth_bytes_per_month: 9,
                distinct_views: 11,
                ws_connections: 13,
                webhook_endpoints: 2,
            }),
        );
        for _ in 0..7 {
            assert!(guards.check_read_query("custom-db").is_ok());
        }
        let rejection = guards.check_read_query("custom-db").expect_err("the 8th request refuses");
        assert_eq!(rejection.kind(), GuardKind::ReadQueryRate);
        assert_eq!(rejection.limit(), 7);
        assert_eq!(rejection.code(), "abuse_guard_exceeded");
        assert!(rejection.fix().contains("custom"), "the fix names the custom row");
        assert!(rejection.message().contains("8/7"), "the message names use and ceiling");

        guards.set_plan(
            "deny-db",
            Plan::Custom(AbuseLimits {
                read_query_rps: 0,
                direct_read_bandwidth_bytes_per_month: 0,
                distinct_views: 0,
                ws_connections: 0,
                webhook_endpoints: 0,
            }),
        );
        assert!(guards.check_read_query("deny-db").is_err(), "zero custom denies reads");
        assert!(guards.acquire_ws("deny-db").is_err(), "zero custom denies sockets");
        assert!(guards.record_direct_read_bytes("deny-db", 0).is_err(), "zero denies bytes too");
    }

    #[test]
    fn scale_limits_are_explicitly_owner_tuned() {
        let guards = AbuseGuards::new();
        guards.set_plan("scale-db", Plan::Scale);
        assert_eq!(guards.limits_of("scale-db"), AbuseLimits::ZERO);
        assert!(guards.check_read_query("scale-db").is_err());

        let limits = AbuseLimits {
            read_query_rps: 2,
            direct_read_bandwidth_bytes_per_month: 3,
            distinct_views: 4,
            ws_connections: 5,
            webhook_endpoints: 6,
        };
        guards.set_limits_override("scale-db", limits);
        assert_eq!(guards.effective_limits("scale-db"), limits);
        assert!(guards.check_read_query("scale-db").is_ok());
        guards.clear_limits("scale-db");
        assert_eq!(guards.limits_of("scale-db"), AbuseLimits::ZERO);
    }

    #[test]
    fn unknown_databases_fail_closed_on_free() {
        let guards = AbuseGuards::new();
        assert_eq!(guards.plan_of("never-named"), Plan::Free);
        for _ in 0..100 {
            assert!(guards.check_read_query("never-named").is_ok());
        }
        assert!(guards.check_read_query("never-named").is_err(), "Free admits 100 req/s");
        assert!(guards.check_read_query("").is_err(), "empty scopes refuse");
    }

    #[test]
    fn bandwidth_accounting_is_exact_at_the_boundary() {
        let guards = AbuseGuards::new();
        guards.set_plan(
            "bytes",
            Plan::Custom(AbuseLimits {
                read_query_rps: u64::MAX,
                direct_read_bandwidth_bytes_per_month: 1_000,
                distinct_views: u64::MAX,
                ws_connections: u64::MAX,
                webhook_endpoints: u64::MAX,
            }),
        );
        assert!(guards.record_direct_read_bytes("bytes", 999).is_ok());
        assert!(guards.record_direct_read_bytes("bytes", 1).is_ok(), "the boundary byte lands");
        assert_eq!(guards.bandwidth_used("bytes"), 1_000);
        let rejection = guards
            .record_direct_read_bytes("bytes", 1)
            .expect_err("the byte past the ceiling refuses");
        assert_eq!(rejection.kind(), GuardKind::DirectReadBandwidth);
        assert_eq!(guards.bandwidth_used("bytes"), 1_000, "refused bytes record nothing");
        assert!(rejection.fix().contains("CDN"), "the fix names the CDN remedy");
        guards.reset_bandwidth("bytes");
        assert_eq!(guards.bandwidth_used("bytes"), 0);
        assert!(guards.record_direct_read_bytes("bytes", 1_000).is_ok(), "rotation reopens");
    }

    #[test]
    fn bandwidth_counter_never_wraps_past_a_maximum_ceiling() {
        let guards = AbuseGuards::new();
        guards.set_plan(
            "max-bytes",
            Plan::Custom(AbuseLimits {
                read_query_rps: 1,
                direct_read_bandwidth_bytes_per_month: u64::MAX,
                distinct_views: 1,
                ws_connections: 1,
                webhook_endpoints: 1,
            }),
        );
        assert!(guards.record_direct_read_bytes("max-bytes", u64::MAX).is_ok());
        let rejection = guards
            .record_direct_read_bytes("max-bytes", 1)
            .expect_err("bytes after u64::MAX must refuse");
        assert_eq!(rejection.limit(), u64::MAX);
        assert_eq!(guards.bandwidth_used("max-bytes"), u64::MAX);
    }

    #[test]
    fn views_sockets_and_endpoints_enforce_distinct_bounds() {
        let guards = AbuseGuards::new();
        assert!(guards.attach_view("v", "a").is_ok());
        assert!(guards.attach_view("v", "a").is_ok(), "re-attach is idempotent");
        assert_eq!(guards.view_count("v"), 1);
        assert!(guards.acquire_ws("v").is_ok());
        assert_eq!(guards.ws_count("v"), 1);
        guards.release_ws("v");
        assert_eq!(guards.ws_count("v"), 0);
        guards.release_ws("v");
        assert_eq!(guards.ws_count("v"), 0, "releasing empty never underflows");

        // Free: 1k views, 100 sockets, 3 endpoints.
        for index in 0..1_000 {
            assert!(guards.attach_view("free-db", &format!("view-{index}")).is_ok());
        }
        assert!(guards.attach_view("free-db", "one-too-many").is_err());
        for _ in 0..100 {
            assert!(guards.acquire_ws("free-db").is_ok());
        }
        assert!(guards.acquire_ws("free-db").is_err(), "the 101st socket refuses");
        for endpoint in ["a", "b", "c"] {
            assert!(guards.register_webhook("free-db", endpoint).is_ok());
        }
        let rejection =
            guards.register_webhook("free-db", "d").expect_err("the 4th endpoint refuses");
        assert_eq!(rejection.kind(), GuardKind::WebhookEndpoints);
        assert!(rejection.fix().contains("Free 3"), "the fix names the D-64 row");
        guards.detach_view("free-db", "view-0");
        assert!(guards.attach_view("free-db", "replacement").is_ok(), "detach frees a slot");
        guards.unregister_webhook("free-db", "a");
        assert_eq!(guards.webhook_count("free-db"), 2);
        assert!(guards.register_webhook("free-db", "d").is_ok(), "unregister frees a slot");
    }

    #[test]
    fn neighbour_flood_cannot_consume_the_victim_budget() {
        let guards = AbuseGuards::new();
        guards.set_plan("victim", Plan::Free);
        guards.set_plan("attacker", Plan::Free);
        for _ in 0..100 {
            assert!(guards.check_read_query("attacker").is_ok());
        }
        assert!(guards.check_read_query("attacker").is_err(), "the attacker is throttled");
        for _ in 0..100 {
            assert!(guards.check_read_query("victim").is_ok(), "the victim is unaffected");
        }
        assert!(guards.record_direct_read_bytes("attacker", 50 * GB).is_ok());
        assert!(guards.record_direct_read_bytes("attacker", 1).is_err());
        assert!(guards.record_direct_read_bytes("victim", 1).is_ok());
    }

    #[test]
    fn governance_budgets_render_systemd_cgroup_weights() {
        let free = TenantBudget::for_plan(Plan::Free);
        assert_eq!((free.cpu_weight(), free.io_weight()), (25, 25));
        assert_eq!(free.memory_max_bytes(), 256 * 1024 * 1024);
        let pro = TenantBudget::for_plan(Plan::Pro);
        assert!(pro.cpu_weight() > free.cpu_weight(), "higher plans weigh more");
        assert!(TenantBudget::custom(0, 1024 * 1024 * 1024, 100).is_none(), "zero weight refuses");
        assert!(TenantBudget::custom(100, 1024, 100).is_none(), "tiny memory refuses");
        let custom = TenantBudget::custom(250, 2 * 1024 * 1024 * 1024, 250)
            .expect("a bounded budget builds");
        assert!(custom.admits_memory(1024));
        assert!(!custom.admits_memory(u64::MAX));
        assert_eq!(custom.cpu_share_milli(2_000, 500), 1_000);
        assert_eq!(custom.cpu_share_milli(2_000, 0), 0, "zero weight sum admits nothing");

        let unit = pro.render_systemd_slice("acme\nCPUWeight=9999");
        assert!(unit.contains("CPUWeight=400"), "the unit carries the share: {unit}");
        assert!(unit.contains("MemoryMax=4294967296"), "the unit carries the cap: {unit}");
        assert!(unit.contains("IOWeight=400"), "the unit carries the I/O share: {unit}");
        assert!(!unit.contains("CPUWeight=9999"), "injected directives never survive: {unit}");
        assert_eq!(
            unit.lines().count(),
            5,
            "a hostile tenant name cannot smuggle unit lines: {unit}"
        );

        let guards = AbuseGuards::new();
        assert_eq!(guards.tenant_budget("unknown"), TenantBudget::for_plan(Plan::Free));
        guards.set_tenant_budget("acme", custom);
        assert_eq!(guards.tenant_budget("acme"), custom);
    }

    #[test]
    fn victim_p99_bound_holds_at_the_edges() {
        assert!(victim_p99_within_2x_solo(Duration::from_millis(10), Duration::from_millis(20)));
        assert!(!victim_p99_within_2x_solo(Duration::from_millis(10), Duration::from_millis(21)));
        assert!(victim_p99_within_2x_solo(Duration::ZERO, Duration::ZERO));
        assert!(!victim_p99_within_2x_solo(Duration::ZERO, Duration::from_nanos(1)));
    }

    /// The adversarial-neighbour test (E-10): while one tenant floods its
    /// own Free budget on another thread, the victim's p99 stays within
    /// twice its solo p99. Sharded budgets make this structural, not lucky:
    /// the flood spends exactly the attacker's shard.
    #[test]
    fn adversarial_neighbour_keeps_victim_p99_within_2x_solo() {
        let guards = Arc::new(AbuseGuards::new());
        guards.set_plan("victim", Plan::Pro);
        guards.set_plan("attacker", Plan::Free);
        let probe = |database: &str| {
            let start = Instant::now();
            let _ = guards.check_read_query(database);
            let mut accumulator = 0u64;
            for step in 0..512u64 {
                accumulator = accumulator.wrapping_add(step.wrapping_mul(0x9E37_79B9));
            }
            std::hint::black_box(accumulator);
            start.elapsed()
        };
        for _ in 0..200 {
            probe("victim");
            let _ = guards.check_read_query("attacker");
        }
        // Pro admits 5_000/s: 400 solo + 400 victim samples stay well
        // inside one window, so every victim probe is admitted in both legs.
        let solo = p99((0..400).map(|_| probe("victim")).collect());
        let stop = Arc::new(AtomicBool::new(false));
        let flood = {
            let guards = Arc::clone(&guards);
            let stop = Arc::clone(&stop);
            std::thread::spawn(move || {
                let mut rounds = 0u64;
                while !stop.load(Ordering::Relaxed) && rounds < 200_000 {
                    let _ = guards.check_read_query("attacker");
                    let _ = guards.record_direct_read_bytes("attacker", 0);
                    rounds += 1;
                }
            })
        };
        let victim = p99((0..400).map(|_| probe("victim")).collect());
        stop.store(true, Ordering::Relaxed);
        flood.join().expect("the flood thread joins");
        eprintln!("guard latency: solo p99={solo:?}, neighbour p99={victim:?}");
        assert!(
            victim_p99_within_2x_solo(solo, victim),
            "victim p99 {victim:?} exceeds 2x solo p99 {solo:?}"
        );
    }
}
