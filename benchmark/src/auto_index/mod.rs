//! Durable automatic-index coordinator (ADR 0045 / D-31).
//!
//! This module is the production boundary between the index advisor and the
//! SQLite engine.  It deliberately contains no process-local authority: a
//! coordinator can only move an index through its lifecycle when the caller
//! supplies all required durable hooks.  In particular, an absent journal,
//! publication transaction, planner visibility hook, or logical-cost hook is
//! reported as [`CoordinatorError::MissingHook`] and the transition is
//! refused.
//!
//! The query optimizer may use the value types in this module to derive a
//! workload and recommendation.  The storage adapter owns bytes, SQLite
//! transactions, and physical indexes.  The adapter methods documented on
//! [`StorageAdapter`] are the exact integration contract; they are kept
//! small so a storage failure cannot be mistaken for a successful lifecycle
//! transition.

use std::collections::{BTreeMap, BTreeSet, VecDeque};

/// Maximum templates retained by one database observer.
pub const OBSERVATION_CAPACITY: usize = 4096;
/// Maximum templates in the compressed 95% workload.
pub const COMPRESSED_CAPACITY: usize = 256;
/// Maximum candidate indexes retained per collection.
pub const CANDIDATE_CAPACITY: usize = 32;
/// Maximum visible indexes per collection.
pub const MAX_VISIBLE_INDEXES: usize = 12;
/// Maintenance budget relative to measured base write work.
pub const WRITE_BUDGET_MULTIPLE: f64 = 2.0;
/// Logical-work regression guard.
pub const REGRESSION_FRACTION: f64 = 0.05;
/// Minimum shadow samples per side for common templates.
pub const MIN_SHADOW_SAMPLES: usize = 20;
/// Minimum improvement required for activation.
pub const MIN_IMPROVEMENT_FRACTION: f64 = 0.20;
/// Retention before a hidden physical index is dropped.
pub const DROP_GRACE_MILLIS: u64 = 24 * 60 * 60 * 1000;
/// Retention before an unused index is hidden.
pub const RETIREMENT_MILLIS: u64 = 7 * 24 * 60 * 60 * 1000;

/// Which production hook a coordinator operation needs.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum HookKind {
    /// Durable lifecycle journal load and append.
    Journal,
    /// Atomic write-transaction catch-up and extraction maintenance.
    PublicationTransaction,
    /// SQLite planner visibility and plan-pin installation.
    Planner,
    /// SQLite logical pages/rows execution counters.
    LogicalCost,
    /// Bounded sequence-ordered backfill.
    Backfill,
}

/// Presence of the production hooks.  The engine passes this value at open;
/// a false bit is an explicit configuration error, never a permission to
/// fall back to an in-memory coordinator.
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct HookAvailability {
    /// Lifecycle journal is durable and replayable.
    pub journal: bool,
    /// Write publication transaction maintains descriptors and indexes.
    pub publication_transaction: bool,
    /// Planner can hide/show indexes and install pins.
    pub planner: bool,
    /// Statement counters provide pages and rows.
    pub logical_cost: bool,
    /// Backfill reads contiguous journal sequence ranges.
    pub backfill: bool,
}

impl HookAvailability {
    /// Validates that all D-31 hooks are installed.
    pub fn validate(self) -> Result<(), CoordinatorError> {
        if !self.journal { return Err(CoordinatorError::MissingHook(HookKind::Journal)); }
        if !self.publication_transaction { return Err(CoordinatorError::MissingHook(HookKind::PublicationTransaction)); }
        if !self.planner { return Err(CoordinatorError::MissingHook(HookKind::Planner)); }
        if !self.logical_cost { return Err(CoordinatorError::MissingHook(HookKind::LogicalCost)); }
        if !self.backfill { return Err(CoordinatorError::MissingHook(HookKind::Backfill)); }
        Ok(())
    }
}

/// A typed error from an unavailable or rejecting integration hook.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum HookError {
    /// The requested operation is not installed in the member.
    Unavailable,
    /// The hook rejected the operation without changing durable state.
    Refused,
    /// The hook supplied a sequence that violates the coordinator contract.
    SequenceConflict,
}

/// Errors from the durable coordinator.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum CoordinatorError {
    /// A required hook was absent.  No state transition was attempted.
    MissingHook(HookKind),
    /// A hook returned a typed failure.
    Hook(HookError),
    /// A lifecycle transition was not legal for the current state.
    InvalidTransition,
    /// A watermark moved backwards, skipped a sequence, or exceeded target.
    InvalidWatermark,
    /// The candidate would violate the collection write budget or cap.
    WriteBudgetExceeded,
    /// Shadow evidence was malformed, capped, or insufficient.
    InsufficientEvidence,
    /// A journal event was duplicated with different content or reordered.
    JournalConflict,
}

impl From<HookError> for CoordinatorError {
    fn from(error: HookError) -> Self {
        Self::Hook(error)
    }
}

/// Logical work measured by SQLite's statement counters.
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct LogicalCost {
    /// SQLite pages read.
    pub pages_read: u64,
    /// SQLite rows examined.
    pub rows_examined: u64,
}

impl LogicalCost {
    /// Creates a logical-cost sample.
    pub const fn new(pages_read: u64, rows_examined: u64) -> Self {
        Self { pages_read, rows_examined }
    }
    /// Returns pages plus rows, saturating on overflow.
    pub const fn total(self) -> u64 {
        self.pages_read.saturating_add(self.rows_examined)
    }
}

/// One normalized predicate in a SQL template.
#[derive(Clone, Debug, Eq, Ord, PartialEq, PartialOrd)]
pub struct Predicate {
    /// Extracted field name.
    pub field: String,
    /// Whether this is equality, range, or full-text matching.
    pub kind: PredicateKind,
}

/// SQL predicate classes understood by candidate derivation.
#[derive(Clone, Copy, Debug, Eq, Ord, PartialEq, PartialOrd)]
pub enum PredicateKind {
    /// `=`, `IN`, `IS`, or equivalent equality predicate.
    Equality,
    /// One range predicate (`<`, `<=`, `>`, `>=`, `BETWEEN`).
    Range,
    /// SQLite FTS5 `MATCH`.
    FullText,
}

/// A literal-free normalized SQL workload template.
#[derive(Clone, Debug, Eq, Ord, PartialEq, PartialOrd)]
pub struct Template {
    /// Collection/table name.
    pub collection: String,
    /// Predicates in canonical SQL order.
    pub predicates: Vec<Predicate>,
    /// `ORDER BY` fields in canonical order.
    pub ordering: Vec<String>,
    /// Projected extracted fields, sorted for identity; `None` means body.
    pub projection: Option<Vec<String>>,
}

impl Template {
    /// Returns whether the template includes FTS5 matching.
    pub fn has_full_text(&self) -> bool {
        self.predicates.iter().any(|p| p.kind == PredicateKind::FullText)
    }
}

/// A bounded execution observation.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct Observation {
    /// Normalized SQL template.
    pub template: Template,
    /// Logical SQLite work.
    pub cost: LogicalCost,
    /// Rows returned.
    pub rows_returned: u64,
    /// Index used, if any.
    pub index: Option<String>,
    /// Durable observation timestamp in milliseconds.
    pub at_millis: u64,
}

/// A bounded workload observer.  Values are copied only into fixed sketches;
/// document bodies never enter this structure.
#[derive(Clone, Debug)]
pub struct WorkloadSketch {
    ring: VecDeque<Observation>,
    columns: BTreeMap<(String, String), ColumnSketch>,
    capacity: usize,
}

impl Default for WorkloadSketch {
    fn default() -> Self {
        Self { ring: VecDeque::with_capacity(OBSERVATION_CAPACITY), columns: BTreeMap::new(), capacity: OBSERVATION_CAPACITY }
    }
}

impl WorkloadSketch {
    /// Creates a bounded observer, clamping capacity to the engine bound.
    pub fn new(capacity: usize) -> Self {
        Self { capacity: capacity.clamp(1, OBSERVATION_CAPACITY), ..Self::default() }
    }
    /// Records an execution and evicts the oldest observation at the bound.
    pub fn observe(&mut self, observation: Observation) {
        self.ring.push_back(observation);
        while self.ring.len() > self.capacity { self.ring.pop_front(); }
    }
    /// Adds an already-extracted value to the per-column sketches.
    pub fn observe_value(&mut self, collection: &str, field: &str, value: &[u8]) {
        self.columns.entry((collection.to_owned(), field.to_owned())).or_default().observe(value);
    }
    /// Returns retained observations in chronological order.
    pub fn observations(&self) -> impl Iterator<Item = &Observation> { self.ring.iter() }
    /// Returns a column sketch, if one has observed values.
    pub fn column(&self, collection: &str, field: &str) -> Option<&ColumnSketch> {
        self.columns.get(&(collection.to_owned(), field.to_owned()))
    }
    /// Compresses observations into the heaviest templates covering `share`.
    pub fn compress(&self, now_millis: u64, window_millis: u64, share: f64) -> Vec<WeightedTemplate> {
        if window_millis == 0 || !(0.0 < share && share <= 1.0) { return Vec::new(); }
        let mut weights: BTreeMap<Template, (u64, u64)> = BTreeMap::new();
        for observation in &self.ring {
            if now_millis.saturating_sub(observation.at_millis) > window_millis { continue; }
            let row = weights.entry(observation.template.clone()).or_default();
            row.0 = row.0.saturating_add(1);
            row.1 = row.1.saturating_add(observation.cost.total());
        }
        let mut by_collection: BTreeMap<String, Vec<WeightedTemplate>> = BTreeMap::new();
        for (template, (frequency, work)) in weights {
            let weight = (frequency as f64) * (work as f64 / frequency.max(1) as f64);
            by_collection.entry(template.collection.clone()).or_default().push(WeightedTemplate { template, frequency, weight });
        }
        let mut result = Vec::new();
        for (_, mut entries) in by_collection {
            entries.sort_by(|a, b| b.weight.total_cmp(&a.weight).then_with(|| a.template.cmp(&b.template)));
            let total: f64 = entries.iter().map(|e| e.weight).sum();
            let target = total * share;
            let mut covered = 0.0;
            for item in entries {
                if result.len() >= COMPRESSED_CAPACITY { break; }
                covered += item.weight;
                result.push(item);
                // Allow the exact 95% boundary despite decimal rounding of
                // the frequency×cost product.
                if covered + 1e-12 * total >= target { break; }
            }
        }
        result
    }
}

/// A compressed template and its frequency/cost weight.
#[derive(Clone, Debug, PartialEq)]
pub struct WeightedTemplate {
    /// Normalized template.
    pub template: Template,
    /// Number of executions in the window.
    pub frequency: u64,
    /// Frequency multiplied by average logical work.
    pub weight: f64,
}

/// Bounded distinct-count and histogram sketch for one extracted field.
#[derive(Clone, Debug)]
pub struct ColumnSketch {
    registers: [u8; 64],
    histogram: BTreeMap<Vec<u8>, u64>,
    total: u64,
}

impl Default for ColumnSketch {
    fn default() -> Self { Self { registers: [0; 64], histogram: BTreeMap::new(), total: 0 } }
}

impl ColumnSketch {
    /// Observes a value without retaining it in the distinct sketch.
    pub fn observe(&mut self, value: &[u8]) {
        let hash = fnv(value);
        let slot = (hash as usize) & 63;
        let rank = (hash >> 6).leading_zeros().saturating_sub(6).saturating_add(1).min(64) as u8;
        self.registers[slot] = self.registers[slot].max(rank.max(1));
        self.total = self.total.saturating_add(1);
        if value.len() <= 256 {
            let next = self.histogram.get(value).copied().unwrap_or(0).saturating_add(1);
            self.histogram.insert(value.to_vec(), next);
            while self.histogram.len() > 128 {
                if let Some(key) = self.histogram.iter().min_by_key(|(_, count)| **count).map(|(k, _)| k.clone()) { self.histogram.remove(&key); } else { break; }
            }
        }
    }
    /// Returns an approximate distinct count.
    pub fn distinct(&self) -> f64 {
        let zero = self.registers.iter().filter(|r| **r == 0).count() as f64;
        let harmonic: f64 = self.registers.iter().map(|r| 2.0_f64.powi(-i32::from(*r))).sum();
        if harmonic == 0.0 { return f64::INFINITY; }
        let raw = 0.673 * 4096.0 / harmonic;
        if raw <= 160.0 && zero > 0.0 { 64.0 * (64.0 / zero).ln() } else { raw }
    }
    /// Number of values observed.
    pub const fn total(&self) -> u64 { self.total }
}

fn fnv(bytes: &[u8]) -> u64 {
    let mut hash = 0xcbf29ce484222325_u64;
    for byte in bytes { hash = (hash ^ u64::from(*byte)).wrapping_mul(0x100000001b3); }
    hash
}

/// Candidate index kind.
#[derive(Clone, Copy, Debug, Eq, Ord, PartialEq, PartialOrd)]
pub enum CandidateKind { /// SQLite B-tree index. 
    BTree,
    /// SQLite FTS5 virtual table.
    Fts,
}

/// One SQL-derived index candidate.
#[derive(Clone, Debug, Eq, Ord, PartialEq, PartialOrd)]
pub struct Candidate {
    /// Collection/table.
    pub collection: String,
    /// Ordered extracted fields.
    pub fields: Vec<String>,
    /// B-tree or FTS5.
    pub kind: CandidateKind,
    /// Whether a missing descriptor must be journaled first.
    pub needs_descriptor: bool,
    /// Estimated maintenance cost per write.
    pub maintenance_cost: u64,
}

/// Derives equality → range → ordering → covering candidates, including FTS5.
pub fn derive_candidates(template: &Template, extracted: &BTreeSet<String>) -> Vec<Candidate> {
    if template.has_full_text() {
        return template.predicates.iter().find(|p| p.kind == PredicateKind::FullText).filter(|p| extracted.contains(&p.field)).map(|p| vec![Candidate { collection: template.collection.clone(), fields: vec![p.field.clone()], kind: CandidateKind::Fts, needs_descriptor: false, maintenance_cost: 1 }]).unwrap_or_default();
    }
    let mut fields = Vec::new();
    let mut needs_descriptor = false;
    for predicate in &template.predicates {
        if predicate.kind == PredicateKind::Equality || predicate.kind == PredicateKind::Range {
            if !fields.contains(&predicate.field) { fields.push(predicate.field.clone()); needs_descriptor |= !extracted.contains(&predicate.field); }
            if predicate.kind == PredicateKind::Range { break; }
        }
    }
    for field in &template.ordering { if !fields.contains(field) { fields.push(field.clone()); needs_descriptor |= !extracted.contains(field); } }
    if fields.is_empty() { return Vec::new(); }
    let base = Candidate { collection: template.collection.clone(), fields: fields.clone(), kind: CandidateKind::BTree, needs_descriptor, maintenance_cost: fields.len() as u64 };
    let mut result = vec![base];
    if let Some(projection) = &template.projection {
        let mut covering = fields;
        for field in projection { if !covering.contains(field) { covering.push(field.clone()); } }
        if covering.len() <= 40 { let maintenance_cost = covering.len() as u64; result.push(Candidate { collection: template.collection.clone(), fields: covering, kind: CandidateKind::BTree, needs_descriptor, maintenance_cost }); }
    }
    result
}

/// Context used by the deterministic C²UCB-style greedy selector.
#[derive(Clone, Copy, Debug, Default)]
pub struct CandidateContext {
    /// Weighted workload benefit.
    pub benefit: f64,
    /// Uncertainty bonus (deliberate exploration).
    pub uncertainty: f64,
    /// Estimated collection selectivity.
    pub selectivity: f64,
    /// Collection row count.
    pub rows: u64,
}

/// Write-budget accounting for one collection.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct WriteBudget { /// Base write work. 
    pub base_cost: f64,
    /// Current visible-index maintenance work.
    pub maintenance_cost: f64,
    /// Number of visible indexes.
    pub visible_indexes: usize,
}

impl WriteBudget {
    /// Returns whether an index with `cost` may be activated.
    pub fn allows(self, cost: f64) -> bool {
        self.base_cost.is_finite() && self.base_cost >= 0.0 && cost.is_finite() && cost >= 0.0 && self.visible_indexes < MAX_VISIBLE_INDEXES && self.maintenance_cost + cost <= self.base_cost * WRITE_BUDGET_MULTIPLE
    }
    /// Accounts one activated index.
    pub fn account(&mut self, cost: f64) -> Result<(), CoordinatorError> {
        if !self.allows(cost) { return Err(CoordinatorError::WriteBudgetExceeded); }
        self.maintenance_cost += cost;
        self.visible_indexes += 1;
        Ok(())
    }
}

/// Selects the highest uncertainty-weighted benefit candidate that fits.
pub fn select_candidate<'a>(candidates: &'a [Candidate], contexts: &[CandidateContext], budget: WriteBudget) -> Result<Option<&'a Candidate>, CoordinatorError> {
    if candidates.len() != contexts.len() { return Err(CoordinatorError::InvalidTransition); }
    candidates.iter().zip(contexts).enumerate().filter(|(_, (candidate, _))| budget.allows(candidate.maintenance_cost as f64)).max_by(|(ia, (_, a)), (ib, (_, b))| {
        let sa = a.benefit + a.uncertainty * (1.0 + a.selectivity.max(0.0));
        let sb = b.benefit + b.uncertainty * (1.0 + b.selectivity.max(0.0));
        sa.total_cmp(&sb).then_with(|| ib.cmp(ia))
    }).map(|(_, (candidate, _))| candidate).map_or(Ok(None), |candidate| Ok(Some(candidate)))
}

/// Exact disjoint plan for an invisible index's backfill tail.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct HybridPlan { /// Last sequence read from the index. 
    pub index_through: u64,
    /// First sequence read from the base collection.
    pub scan_from: u64,
    /// Last sequence in the bounded scan tail.
    pub scan_through: u64,
}

/// Lifecycle phase persisted for every candidate.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum Phase { Observing, Selected, Building, Shadowing, Active, Invisible, Abandoned }

/// Durable state of one candidate index.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct IndexState {
    /// Stable physical identity.
    pub id: String,
    /// Collection/table.
    pub collection: String,
    /// Lifecycle phase.
    pub phase: Phase,
    /// Build target sequence.
    pub target: u64,
    /// Last atomically applied sequence.
    pub covered: u64,
    /// Planner visibility bit (must be false until Active).
    pub visible: bool,
    /// Generation used to invalidate pins.
    pub generation: u64,
    /// Last usage timestamp.
    pub last_used_millis: u64,
    /// Timestamp hidden for delayed physical drop.
    pub invisible_since_millis: Option<u64>,
}

impl IndexState {
    /// Returns a hybrid plan when the uncovered tail fits the scan cap.
    pub fn hybrid_plan(&self, scan_cap: u64) -> Option<HybridPlan> {
        if self.phase != Phase::Building || self.covered >= self.target { return None; }
        let tail = self.target - self.covered;
        (tail <= scan_cap).then(|| HybridPlan { index_through: self.covered, scan_from: self.covered.checked_add(1).unwrap_or(u64::MAX), scan_through: self.target })
    }
}

/// Welch evidence for one template/candidate shadow replay.
#[derive(Clone, Copy, Debug, PartialEq)]
pub struct WelchEvidence {
    /// Baseline mean total work.
    pub baseline_mean: f64,
    /// Candidate mean total work.
    pub candidate_mean: f64,
    /// Fractional improvement.
    pub improvement: f64,
    /// Welch statistic.
    pub statistic: f64,
    /// Hard pages/rows/p95 guard crossed.
    pub regression: bool,
}

/// Shadow outcome.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum ShadowOutcome { Activate, ActivateWithPins, Abandon }

/// Evaluates bounded logical-cost samples using a conservative Welch gate.
pub fn verify_shadow(baseline: &[LogicalCost], candidate: &[LogicalCost]) -> Result<(ShadowOutcome, WelchEvidence), CoordinatorError> {
    if baseline.len() < MIN_SHADOW_SAMPLES || candidate.len() < MIN_SHADOW_SAMPLES { return Err(CoordinatorError::InsufficientEvidence); }
    let mean = |samples: &[LogicalCost]| samples.iter().map(|s| s.total() as f64).sum::<f64>() / samples.len() as f64;
    let bm = mean(baseline); let cm = mean(candidate);
    if !bm.is_finite() || !cm.is_finite() || bm <= 0.0 { return Err(CoordinatorError::InsufficientEvidence); }
    let variance = |samples: &[LogicalCost], m: f64| samples.iter().map(|s| { let d = s.total() as f64 - m; d * d }).sum::<f64>() / (samples.len() - 1) as f64;
    let se = variance(baseline, bm) / baseline.len() as f64 + variance(candidate, cm) / candidate.len() as f64;
    let statistic = if se == 0.0 { if (bm - cm).abs() < f64::EPSILON { 0.0 } else { f64::INFINITY } } else { (bm - cm).abs() / se.sqrt() };
    if !statistic.is_finite() && (bm - cm).abs() < f64::EPSILON { return Err(CoordinatorError::InsufficientEvidence); }
    let improvement = (bm - cm) / bm;
    let p95 = |samples: &[LogicalCost]| { let mut values: Vec<u64> = samples.iter().map(|s| s.total()).collect(); values.sort_unstable(); values[((values.len() - 1) * 95) / 100] as f64 };
    let regression = cm > bm * (1.0 + REGRESSION_FRACTION) || p95(candidate) > p95(baseline) * (1.0 + REGRESSION_FRACTION);
    let evidence = WelchEvidence { baseline_mean: bm, candidate_mean: cm, improvement, statistic, regression };
    let significant = statistic >= 2.0;
    let outcome = if regression { ShadowOutcome::ActivateWithPins } else if significant && improvement >= MIN_IMPROVEMENT_FRACTION { ShadowOutcome::Activate } else { ShadowOutcome::Abandon };
    Ok((outcome, evidence))
}

/// A durable lifecycle event.  The adapter must append it before the caller
/// exposes the resulting state to the planner.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct LifecycleEvent {
    /// Monotone per-database event sequence.
    pub sequence: u64,
    /// Affected index identity.
    pub index: String,
    /// New phase.
    pub phase: Phase,
    /// Build watermark, if applicable.
    pub watermark: Option<u64>,
    /// Build target sequence, present on the initial `Building` event.
    pub target: Option<u64>,
    /// Candidate/evidence digest.
    pub evidence_digest: [u8; 32],
    /// Durable timestamp.
    pub at_millis: u64,
}

/// Exact storage hooks required by the coordinator.
pub trait StorageAdapter {
    /// Loads lifecycle events from the publication journal in sequence order.
    fn load_lifecycle(&mut self) -> Result<Vec<LifecycleEvent>, HookError>;
    /// Appends one event atomically with any descriptor/catalog mutation.
    fn append_lifecycle(&mut self, event: &LifecycleEvent) -> Result<(), HookError>;
    /// Applies a bounded contiguous batch to an invisible index.
    fn backfill_batch(&mut self, index: &str, first: u64, last: u64) -> Result<(), HookError>;
    /// Catches up and verifies the final tail in the same write transaction.
    fn catch_up_and_verify(&mut self, index: &str, covered: u64, target: u64) -> Result<u64, HookError>;
    /// Changes planner visibility in the same durable catalog.
    fn set_visible(&mut self, index: &str, visible: bool) -> Result<(), HookError>;
    /// Drops physical pages after the invisible grace period.
    fn drop_physical(&mut self, index: &str) -> Result<(), HookError>;
    /// Installs/removes a template-specific baseline plan pin.
    fn set_plan_pin(&mut self, template: &str, index: &str, pinned: bool) -> Result<(), HookError>;
    /// Records logical pages/rows for a normalized template.
    fn record_logical_cost(&mut self, template: &Template, cost: LogicalCost) -> Result<(), HookError>;
    /// Runs a hypothetical compile check without mutating the live schema.
    fn compile_hypothetical(&mut self, template: &Template, candidate: &Candidate) -> Result<bool, HookError>;
}

/// A coordinator with durable lifecycle replay and fail-closed transitions.
pub struct Coordinator<A> { adapter: A, states: BTreeMap<String, IndexState>, next_sequence: u64 }

impl<A: StorageAdapter> Coordinator<A> {
    /// Recovers state from durable events. Reordered or conflicting events
    /// fail closed before any adapter mutation.
    pub fn recover(mut adapter: A) -> Result<Self, CoordinatorError> {
        let events = adapter.load_lifecycle().map_err(CoordinatorError::Hook)?;
        let mut coordinator = Self { adapter, states: BTreeMap::new(), next_sequence: 1 };
        for event in events { coordinator.apply_event(event)?; }
        Ok(coordinator)
    }
    /// Recovers only when the member explicitly reports every required hook.
    pub fn recover_with_hooks(adapter: A, hooks: HookAvailability) -> Result<Self, CoordinatorError> {
        hooks.validate()?;
        Self::recover(adapter)
    }
    /// Returns the adapter for integration-owned maintenance work.
    pub fn adapter_mut(&mut self) -> &mut A { &mut self.adapter }
    /// Returns one recovered index state.
    pub fn state(&self, index: &str) -> Option<&IndexState> { self.states.get(index) }
    /// Records planner use.  The timestamp is durable when the next journal
    /// event is written, so restart cannot make a rare query look unused.
    pub fn note_used(&mut self, index: &str, at_millis: u64) -> Result<(), CoordinatorError> {
        let state = self.states.get_mut(index).ok_or(CoordinatorError::InvalidTransition)?;
        if state.phase != Phase::Active || !state.visible { return Err(CoordinatorError::InvalidTransition); }
        state.last_used_millis = state.last_used_millis.max(at_millis);
        Ok(())
    }
    /// Applies an event to memory after journal replay validation.
    fn apply_event(&mut self, event: LifecycleEvent) -> Result<(), CoordinatorError> {
        if event.sequence != self.next_sequence { return Err(CoordinatorError::JournalConflict); }
        self.next_sequence = self.next_sequence.checked_add(1).ok_or(CoordinatorError::JournalConflict)?;
        let state = self.states.entry(event.index.clone()).or_insert_with(|| IndexState { id: event.index.clone(), collection: String::new(), phase: Phase::Observing, target: 0, covered: 0, visible: false, generation: 0, last_used_millis: 0, invisible_since_millis: None });
        let legal = match (state.phase, event.phase) {
            (Phase::Observing, Phase::Selected)
            | (Phase::Selected, Phase::Building)
            | (Phase::Building, Phase::Shadowing)
            | (Phase::Shadowing, Phase::Active)
            | (Phase::Shadowing, Phase::Abandoned)
            | (Phase::Active, Phase::Invisible)
            | (Phase::Invisible, Phase::Abandoned) => true,
            (Phase::Building, Phase::Building) => true,
            // A replay may contain an exact duplicate event; sequence
            // ordering still protects against a conflicting duplicate.
            (from, to) if from == to => true,
            _ => false,
        };
        if !legal { return Err(CoordinatorError::InvalidTransition); }
        if let Some(target) = event.target { state.target = target; }
        if let Some(watermark) = event.watermark {
            if watermark < state.covered { return Err(CoordinatorError::InvalidWatermark); }
            // A target-bearing Building event describes the snapshot boundary;
            // subsequent watermark events describe applied coverage.
            if event.target.is_none() || event.phase != Phase::Building { state.covered = watermark; }
            state.target = state.target.max(watermark);
        }
        state.phase = event.phase;
        state.visible = event.phase == Phase::Active;
        if event.phase == Phase::Invisible { state.invisible_since_millis = Some(event.at_millis); }
        Ok(())
    }
    /// Appends an event, then applies it; a failed append leaves memory unchanged.
    pub fn append(&mut self, mut event: LifecycleEvent) -> Result<(), CoordinatorError> {
        event.sequence = self.next_sequence;
        self.adapter.append_lifecycle(&event).map_err(CoordinatorError::Hook)?;
        self.apply_event(event)
    }
    /// Starts an invisible build at a durable snapshot boundary.
    pub fn start_build(&mut self, index: &str, target: u64, at_millis: u64, digest: [u8; 32]) -> Result<(), CoordinatorError> {
        if target == 0 || self.states.get(index).is_some_and(|state| state.phase != Phase::Selected) { return Err(CoordinatorError::InvalidTransition); }
        self.append(LifecycleEvent { sequence: 0, index: index.to_owned(), phase: Phase::Building, watermark: Some(target), target: Some(target), evidence_digest: digest, at_millis })
    }
    /// Applies one contiguous backfill batch and journals its watermark.
    pub fn advance_backfill(&mut self, index: &str, last: u64, at_millis: u64, digest: [u8; 32]) -> Result<(), CoordinatorError> {
        let state = self.states.get(index).ok_or(CoordinatorError::InvalidTransition)?;
        if state.phase != Phase::Building || last <= state.covered || last > state.target { return Err(CoordinatorError::InvalidWatermark); }
        let first = state.covered.checked_add(1).ok_or(CoordinatorError::InvalidWatermark)?;
        self.adapter.backfill_batch(index, first, last).map_err(CoordinatorError::Hook)?;
        self.append(LifecycleEvent { sequence: 0, index: index.to_owned(), phase: Phase::Building, watermark: Some(last), target: None, evidence_digest: digest, at_millis })
    }
    /// Moves a fully covered invisible build into shadow verification.
    pub fn begin_shadow(&mut self, index: &str, at_millis: u64, digest: [u8; 32]) -> Result<(), CoordinatorError> {
        let state = self.states.get(index).ok_or(CoordinatorError::InvalidTransition)?;
        if state.phase != Phase::Building || state.covered != state.target { return Err(CoordinatorError::InvalidTransition); }
        let covered = state.covered;
        self.append(LifecycleEvent { sequence: 0, index: index.to_owned(), phase: Phase::Shadowing, watermark: Some(covered), target: None, evidence_digest: digest, at_millis })
    }
    /// Performs the final same-transaction catch-up and publishes visibility.
    pub fn activate(&mut self, index: &str, at_millis: u64, digest: [u8; 32]) -> Result<(), CoordinatorError> {
        let state = self.states.get(index).ok_or(CoordinatorError::InvalidTransition)?;
        if state.phase != Phase::Shadowing || state.covered != state.target { return Err(CoordinatorError::InvalidTransition); }
        let covered = self.adapter.catch_up_and_verify(index, state.covered, state.target).map_err(CoordinatorError::Hook)?;
        if covered < state.target { return Err(CoordinatorError::InvalidWatermark); }
        self.adapter.set_visible(index, true).map_err(CoordinatorError::Hook)?;
        self.append(LifecycleEvent { sequence: 0, index: index.to_owned(), phase: Phase::Active, watermark: Some(covered), target: None, evidence_digest: digest, at_millis })
    }
    /// Installs a baseline pin for one exact normalized template.  Pins are
    /// planner state, never a global disable, and therefore cannot expose a
    /// regressed plan for an unrelated query.
    pub fn pin_template(&mut self, template: &str, index: &str) -> Result<(), CoordinatorError> {
        let state = self.states.get(index).ok_or(CoordinatorError::InvalidTransition)?;
        if state.phase != Phase::Active || !state.visible { return Err(CoordinatorError::InvalidTransition); }
        self.adapter.set_plan_pin(template, index, true).map_err(CoordinatorError::Hook)
    }
    /// Hides active indexes that have not been used for the retirement window.
    /// The physical drop remains delayed behind [`DROP_GRACE_MILLIS`].
    pub fn retire_unused(&mut self, now_millis: u64, digest: [u8; 32]) -> Vec<String> {
        let candidates: Vec<String> = self.states.iter().filter(|(_, state)| state.phase == Phase::Active && state.visible && state.last_used_millis != 0 && now_millis.saturating_sub(state.last_used_millis) >= RETIREMENT_MILLIS).map(|(id, _)| id.clone()).collect();
        let mut retired = Vec::new();
        for index in candidates {
            if self.adapter.set_visible(&index, false).is_err() { continue; }
            if self.append(LifecycleEvent { sequence: 0, index: index.clone(), phase: Phase::Invisible, watermark: self.states.get(&index).map(|state| state.covered), target: None, evidence_digest: digest, at_millis: now_millis }).is_ok() { retired.push(index); }
        }
        retired
    }
    /// Retires the narrow side of a verified prefix merge.  The caller must
    /// supply evidence from shadow replay; refusing weak evidence prevents a
    /// catalog-count optimization from becoming a query regression.
    pub fn merge_prefix(&mut self, narrow: &str, wider: &str, read_improvement: f64, write_delta: f64, at_millis: u64, digest: [u8; 32]) -> Result<(), CoordinatorError> {
        if narrow == wider || !read_improvement.is_finite() || !write_delta.is_finite() || read_improvement < REGRESSION_FRACTION || write_delta > REGRESSION_FRACTION { return Err(CoordinatorError::InvalidTransition); }
        let narrow_state = self.states.get(narrow).ok_or(CoordinatorError::InvalidTransition)?;
        let wider_state = self.states.get(wider).ok_or(CoordinatorError::InvalidTransition)?;
        if narrow_state.phase != Phase::Active || wider_state.phase != Phase::Active || !narrow_state.visible || !wider_state.visible { return Err(CoordinatorError::InvalidTransition); }
        let covered = narrow_state.covered;
        self.adapter.set_visible(narrow, false).map_err(CoordinatorError::Hook)?;
        self.append(LifecycleEvent { sequence: 0, index: narrow.to_owned(), phase: Phase::Invisible, watermark: Some(covered), target: None, evidence_digest: digest, at_millis })
    }
    /// Makes an active index invisible immediately and journals the rollback.
    pub fn rollback(&mut self, index: &str, at_millis: u64, digest: [u8; 32]) -> Result<(), CoordinatorError> {
        let state = self.states.get(index).ok_or(CoordinatorError::InvalidTransition)?;
        if state.phase != Phase::Active { return Err(CoordinatorError::InvalidTransition); }
        let covered = state.covered;
        self.adapter.set_visible(index, false).map_err(CoordinatorError::Hook)?;
        self.append(LifecycleEvent { sequence: 0, index: index.to_owned(), phase: Phase::Invisible, watermark: Some(covered), target: None, evidence_digest: digest, at_millis })
    }
    /// Drops an invisible index only after the 24-hour grace period.
    pub fn retire(&mut self, index: &str, now_millis: u64, digest: [u8; 32]) -> Result<(), CoordinatorError> {
        let state = self.states.get(index).ok_or(CoordinatorError::InvalidTransition)?;
        let since = state.invisible_since_millis.ok_or(CoordinatorError::InvalidTransition)?;
        if now_millis.saturating_sub(since) < DROP_GRACE_MILLIS { return Err(CoordinatorError::InvalidTransition); }
        self.adapter.drop_physical(index).map_err(CoordinatorError::Hook)?;
        let covered = state.covered;
        self.append(LifecycleEvent { sequence: 0, index: index.to_owned(), phase: Phase::Abandoned, watermark: Some(covered), target: None, evidence_digest: digest, at_millis: now_millis })
    }
}

#[cfg(test)]
mod tests;
