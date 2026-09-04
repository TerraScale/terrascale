//! Durable automatic-index coordinator (ADR 0045 / D-31).
//!
//! This module is the production boundary between the index advisor and the
//! `SQLite` engine.  It deliberately contains no process-local authority: a
//! coordinator can only move an index through its lifecycle when the caller
//! supplies all required durable hooks.  In particular, an absent journal,
//! publication transaction, planner visibility hook, or logical-cost hook is
//! reported as [`CoordinatorError::MissingHook`] and the transition is
//! refused.
//!
//! The query optimizer may use the value types in this module to derive a
//! workload and recommendation.  The storage adapter owns bytes, `SQLite`
//! transactions, and physical indexes.  The adapter methods documented on
//! [`StorageAdapter`] are the exact integration contract; they are kept
//! small so a storage failure cannot be mistaken for a successful lifecycle
//! transition.

use std::collections::{BTreeMap, BTreeSet, VecDeque};
use std::fs::{File, OpenOptions};
use std::io::{Read, Write};
use std::path::{Path, PathBuf};

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
    /// `SQLite` planner visibility and plan-pin installation.
    Planner,
    /// `SQLite` logical pages/rows execution counters.
    LogicalCost,
    /// Bounded sequence-ordered backfill.
    Backfill,
}

/// Presence of the production hooks.  The engine passes this value at open;
/// a false bit is an explicit configuration error, never a permission to
/// fall back to an in-memory coordinator.
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
#[allow(
    clippy::struct_excessive_bools,
    reason = "five explicit hook-presence bits; a bitmask struct keeps open-time validation total"
)]
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
    ///
    /// # Errors
    ///
    /// Returns [`CoordinatorError::MissingHook`] for the first absent hook.
    pub fn validate(self) -> Result<(), CoordinatorError> {
        if !self.journal {
            return Err(CoordinatorError::MissingHook(HookKind::Journal));
        }
        if !self.publication_transaction {
            return Err(CoordinatorError::MissingHook(HookKind::PublicationTransaction));
        }
        if !self.planner {
            return Err(CoordinatorError::MissingHook(HookKind::Planner));
        }
        if !self.logical_cost {
            return Err(CoordinatorError::MissingHook(HookKind::LogicalCost));
        }
        if !self.backfill {
            return Err(CoordinatorError::MissingHook(HookKind::Backfill));
        }
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

/// Logical work measured by `SQLite`'s statement counters.
#[derive(Clone, Copy, Debug, Default, Eq, PartialEq)]
pub struct LogicalCost {
    /// `SQLite` pages read.
    pub pages_read: u64,
    /// `SQLite` rows examined.
    pub rows_examined: u64,
}

impl LogicalCost {
    /// Creates a logical-cost sample.
    #[must_use]
    pub const fn new(pages_read: u64, rows_examined: u64) -> Self {
        Self { pages_read, rows_examined }
    }
    /// Returns pages plus rows, saturating on overflow.
    #[must_use]
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
    /// `SQLite` FTS5 `MATCH`.
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
    #[must_use]
    pub fn has_full_text(&self) -> bool {
        self.predicates.iter().any(|p| p.kind == PredicateKind::FullText)
    }
}

/// A bounded execution observation.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct Observation {
    /// Normalized SQL template.
    pub template: Template,
    /// Logical `SQLite` work.
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
        Self {
            ring: VecDeque::with_capacity(OBSERVATION_CAPACITY),
            columns: BTreeMap::new(),
            capacity: OBSERVATION_CAPACITY,
        }
    }
}

impl WorkloadSketch {
    /// Creates a bounded observer, clamping capacity to the engine bound.
    #[must_use]
    pub fn new(capacity: usize) -> Self {
        Self { capacity: capacity.clamp(1, OBSERVATION_CAPACITY), ..Self::default() }
    }
    /// Records an execution and evicts the oldest observation at the bound.
    pub fn observe(&mut self, observation: Observation) {
        self.ring.push_back(observation);
        while self.ring.len() > self.capacity {
            self.ring.pop_front();
        }
    }
    /// Adds an already-extracted value to the per-column sketches.
    pub fn observe_value(&mut self, collection: &str, field: &str, value: &[u8]) {
        self.columns.entry((collection.to_owned(), field.to_owned())).or_default().observe(value);
    }
    /// Returns retained observations in chronological order.
    pub fn observations(&self) -> impl Iterator<Item = &Observation> {
        self.ring.iter()
    }
    /// Returns a column sketch, if one has observed values.
    #[must_use]
    pub fn column(&self, collection: &str, field: &str) -> Option<&ColumnSketch> {
        self.columns.get(&(collection.to_owned(), field.to_owned()))
    }
    /// Compresses observations into the heaviest templates covering `share`.
    #[must_use]
    #[allow(clippy::cast_precision_loss, reason = "compression weight; rounding is noise")]
    pub fn compress(
        &self,
        now_millis: u64,
        window_millis: u64,
        share: f64,
    ) -> Vec<WeightedTemplate> {
        if window_millis == 0 || !(0.0 < share && share <= 1.0) {
            return Vec::new();
        }
        let mut weights: BTreeMap<Template, (u64, u64)> = BTreeMap::new();
        for observation in &self.ring {
            if now_millis.saturating_sub(observation.at_millis) > window_millis {
                continue;
            }
            let row = weights.entry(observation.template.clone()).or_default();
            row.0 = row.0.saturating_add(1);
            row.1 = row.1.saturating_add(observation.cost.total());
        }
        let mut by_collection: BTreeMap<String, Vec<WeightedTemplate>> = BTreeMap::new();
        for (template, (frequency, work)) in weights {
            // Saturating: frequencies are bounded by `OBSERVATION_CAPACITY`;
            // the fallback keeps the ranking weight total for oversized inputs.
            let frequency_f = f64::from(u32::try_from(frequency).unwrap_or(u32::MAX));
            let divisor = f64::from(u32::try_from(frequency.max(1)).unwrap_or(u32::MAX));
            let weight = frequency_f * (work as f64 / divisor);
            by_collection.entry(template.collection.clone()).or_default().push(WeightedTemplate {
                template,
                frequency,
                weight,
            });
        }
        let mut result = Vec::new();
        for (_, mut entries) in by_collection {
            entries.sort_by(|a, b| {
                b.weight.total_cmp(&a.weight).then_with(|| a.template.cmp(&b.template))
            });
            let total: f64 = entries.iter().map(|e| e.weight).sum();
            let target = total * share;
            let mut covered = 0.0;
            for item in entries {
                if result.len() >= COMPRESSED_CAPACITY {
                    break;
                }
                covered += item.weight;
                result.push(item);
                // Allow the exact 95% boundary despite decimal rounding of
                // the frequency×cost product.
                if covered + 1e-12 * total >= target {
                    break;
                }
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
    fn default() -> Self {
        Self { registers: [0; 64], histogram: BTreeMap::new(), total: 0 }
    }
}

impl ColumnSketch {
    /// Observes a value without retaining it in the distinct sketch.
    pub fn observe(&mut self, value: &[u8]) {
        let hash = fnv(value);
        // Saturating: the register table is masked below, so the fallback
        // keeps index derivation total on targets with 32-bit pointers.
        let slot = usize::try_from(hash).unwrap_or(usize::MAX) & 63;
        let rank = (hash >> 6).leading_zeros().saturating_sub(6).saturating_add(1).min(64) as u8;
        self.registers[slot] = self.registers[slot].max(rank.max(1));
        self.total = self.total.saturating_add(1);
        if value.len() <= 256 {
            let next = self.histogram.get(value).copied().unwrap_or(0).saturating_add(1);
            self.histogram.insert(value.to_vec(), next);
            while self.histogram.len() > 128 {
                if let Some(key) =
                    self.histogram.iter().min_by_key(|(_, count)| **count).map(|(k, _)| k.clone())
                {
                    self.histogram.remove(&key);
                } else {
                    break;
                }
            }
        }
    }
    /// Returns an approximate distinct count.
    #[must_use]
    pub fn distinct(&self) -> f64 {
        // Saturating: the register table holds 64 entries; the fallback keeps
        // the estimator total for oversized inputs.
        let mut zero_count = 0_u32;
        for register in self.registers {
            zero_count += u32::from(register == 0);
        }
        let zero = f64::from(zero_count);
        let harmonic: f64 = self.registers.iter().map(|r| 2.0_f64.powi(-i32::from(*r))).sum();
        if harmonic == 0.0 {
            return f64::INFINITY;
        }
        let raw = 0.673 * 4096.0 / harmonic;
        if raw <= 160.0 && zero > 0.0 { 64.0 * (64.0 / zero).ln() } else { raw }
    }
    /// Number of values observed.
    #[must_use]
    pub const fn total(&self) -> u64 {
        self.total
    }
}

fn fnv(bytes: &[u8]) -> u64 {
    let mut hash = 0xcbf2_9ce4_8422_2325_u64;
    for byte in bytes {
        hash = (hash ^ u64::from(*byte)).wrapping_mul(0x0100_0000_01b3);
    }
    hash
}

/// Candidate index kind.
#[derive(Clone, Copy, Debug, Eq, Ord, PartialEq, PartialOrd)]
pub enum CandidateKind {
    /// `SQLite` B-tree index.
    BTree,
    /// `SQLite` FTS5 virtual table.
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
#[must_use]
pub fn derive_candidates(template: &Template, extracted: &BTreeSet<String>) -> Vec<Candidate> {
    if template.has_full_text() {
        return template
            .predicates
            .iter()
            .find(|p| p.kind == PredicateKind::FullText)
            .filter(|p| extracted.contains(&p.field))
            .map(|p| {
                vec![Candidate {
                    collection: template.collection.clone(),
                    fields: vec![p.field.clone()],
                    kind: CandidateKind::Fts,
                    needs_descriptor: false,
                    maintenance_cost: 1,
                }]
            })
            .unwrap_or_default();
    }
    let mut fields = Vec::new();
    let mut needs_descriptor = false;
    for predicate in &template.predicates {
        if predicate.kind == PredicateKind::Equality || predicate.kind == PredicateKind::Range {
            if !fields.contains(&predicate.field) {
                fields.push(predicate.field.clone());
                needs_descriptor |= !extracted.contains(&predicate.field);
            }
            if predicate.kind == PredicateKind::Range {
                break;
            }
        }
    }
    for field in &template.ordering {
        if !fields.contains(field) {
            fields.push(field.clone());
            needs_descriptor |= !extracted.contains(field);
        }
    }
    if fields.is_empty() {
        return Vec::new();
    }
    let base = Candidate {
        collection: template.collection.clone(),
        fields: fields.clone(),
        kind: CandidateKind::BTree,
        needs_descriptor,
        maintenance_cost: fields.len() as u64,
    };
    let mut result = vec![base];
    if let Some(projection) = &template.projection {
        let mut covering = fields;
        for field in projection {
            if !covering.contains(field) {
                covering.push(field.clone());
            }
        }
        if covering.len() <= 40 {
            let maintenance_cost = covering.len() as u64;
            result.push(Candidate {
                collection: template.collection.clone(),
                fields: covering,
                kind: CandidateKind::BTree,
                needs_descriptor,
                maintenance_cost,
            });
        }
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
pub struct WriteBudget {
    /// Base write work.
    pub base_cost: f64,
    /// Current visible-index maintenance work.
    pub maintenance_cost: f64,
    /// Number of visible indexes.
    pub visible_indexes: usize,
}

impl WriteBudget {
    /// Returns whether an index with `cost` may be activated.
    #[must_use]
    pub fn allows(self, cost: f64) -> bool {
        self.base_cost.is_finite()
            && self.base_cost >= 0.0
            && cost.is_finite()
            && cost >= 0.0
            && self.visible_indexes < MAX_VISIBLE_INDEXES
            && self.maintenance_cost + cost <= self.base_cost * WRITE_BUDGET_MULTIPLE
    }
    /// Accounts one activated index.
    ///
    /// # Errors
    ///
    /// Returns [`CoordinatorError::WriteBudgetExceeded`] when `cost` does not
    /// fit the collection budget or index cap.
    pub fn account(&mut self, cost: f64) -> Result<(), CoordinatorError> {
        if !self.allows(cost) {
            return Err(CoordinatorError::WriteBudgetExceeded);
        }
        self.maintenance_cost += cost;
        self.visible_indexes += 1;
        Ok(())
    }
}

/// Selects the highest uncertainty-weighted benefit candidate that fits.
///
/// # Errors
///
/// Returns [`CoordinatorError::InvalidTransition`] when `candidates` and
/// `contexts` have different lengths.
pub fn select_candidate<'a>(
    candidates: &'a [Candidate],
    contexts: &[CandidateContext],
    budget: WriteBudget,
) -> Result<Option<&'a Candidate>, CoordinatorError> {
    if candidates.len() != contexts.len() {
        return Err(CoordinatorError::InvalidTransition);
    }
    candidates
        .iter()
        .zip(contexts)
        .enumerate()
        // Saturating: maintenance costs are bounded by the candidate field
        // limit; the fallback keeps selection total for oversized inputs.
        .filter(|(_, (candidate, _))| {
            budget.allows(f64::from(u32::try_from(candidate.maintenance_cost).unwrap_or(u32::MAX)))
        })
        .max_by(|(ia, (_, a)), (ib, (_, b))| {
            let sa = a.benefit + a.uncertainty * (1.0 + a.selectivity.max(0.0));
            let sb = b.benefit + b.uncertainty * (1.0 + b.selectivity.max(0.0));
            sa.total_cmp(&sb).then_with(|| ib.cmp(ia))
        })
        .map(|(_, (candidate, _))| candidate)
        .map_or(Ok(None), |candidate| Ok(Some(candidate)))
}

/// Exact disjoint plan for an invisible index's backfill tail.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct HybridPlan {
    /// Last sequence read from the index.
    pub index_through: u64,
    /// First sequence read from the base collection.
    pub scan_from: u64,
    /// Last sequence in the bounded scan tail.
    pub scan_through: u64,
}

/// Lifecycle phase persisted for every candidate.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum Phase {
    /// Workload observation; initial phase for new index entries.
    Observing,
    /// Candidate chosen and awaiting an invisible build.
    Selected,
    /// Invisible backfill toward the snapshot target.
    Building,
    /// Fully covered build under shadow verification.
    Shadowing,
    /// Planner-visible serving index.
    Active,
    /// Hidden but physically retained for the drop grace period.
    Invisible,
    /// Terminal dropped or rejected index.
    Abandoned,
}

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
    #[must_use]
    pub fn hybrid_plan(&self, scan_cap: u64) -> Option<HybridPlan> {
        if self.phase != Phase::Building || self.covered >= self.target {
            return None;
        }
        let tail = self.target - self.covered;
        (tail <= scan_cap).then(|| HybridPlan {
            index_through: self.covered,
            scan_from: self.covered.saturating_add(1),
            scan_through: self.target,
        })
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
pub enum ShadowOutcome {
    /// Significant improvement without regression; make the index visible.
    Activate,
    /// Regression guard crossed; activate with baseline plan pins.
    ActivateWithPins,
    /// Insufficient evidence; do not activate.
    Abandon,
}

/// Evaluates bounded logical-cost samples using a conservative Welch gate.
///
/// # Errors
///
/// Returns [`CoordinatorError::InsufficientEvidence`] when either side has
/// fewer than [`MIN_SHADOW_SAMPLES`] samples, when a mean is non-finite or
/// non-positive, or when a zero-variance tie leaves no decision.
#[allow(
    clippy::cast_precision_loss,
    reason = "bounded logical counters; precision is below measurement noise"
)]
pub fn verify_shadow(
    baseline: &[LogicalCost],
    candidate: &[LogicalCost],
) -> Result<(ShadowOutcome, WelchEvidence), CoordinatorError> {
    if baseline.len() < MIN_SHADOW_SAMPLES || candidate.len() < MIN_SHADOW_SAMPLES {
        return Err(CoordinatorError::InsufficientEvidence);
    }
    let mean = |samples: &[LogicalCost]| {
        // Saturating: shadow samples are bounded; the fallback keeps the mean
        // total for oversized inputs.
        let count = f64::from(u32::try_from(samples.len()).unwrap_or(u32::MAX));
        samples.iter().map(|s| s.total() as f64).sum::<f64>() / count
    };
    let bm = mean(baseline);
    let cm = mean(candidate);
    if !bm.is_finite() || !cm.is_finite() || bm <= 0.0 {
        return Err(CoordinatorError::InsufficientEvidence);
    }
    let variance = |samples: &[LogicalCost], m: f64| {
        // Saturating: shadow samples are bounded; the fallback keeps the
        // variance total for oversized inputs.
        let count = f64::from(u32::try_from(samples.len()).unwrap_or(u32::MAX));
        samples
            .iter()
            .map(|s| {
                let d = s.total() as f64 - m;
                d * d
            })
            .sum::<f64>()
            / (count - 1.0)
    };
    // Saturating: shadow samples are bounded; the fallback keeps the
    // standard-error total for oversized inputs.
    let baseline_count = f64::from(u32::try_from(baseline.len()).unwrap_or(u32::MAX));
    let candidate_count = f64::from(u32::try_from(candidate.len()).unwrap_or(u32::MAX));
    let se = variance(baseline, bm) / baseline_count + variance(candidate, cm) / candidate_count;
    let statistic = if se == 0.0 {
        if (bm - cm).abs() < f64::EPSILON { 0.0 } else { f64::INFINITY }
    } else {
        (bm - cm).abs() / se.sqrt()
    };
    if !statistic.is_finite() && (bm - cm).abs() < f64::EPSILON {
        return Err(CoordinatorError::InsufficientEvidence);
    }
    let improvement = (bm - cm) / bm;
    let p95 = |samples: &[LogicalCost]| {
        let mut values: Vec<u64> = samples.iter().map(|s| s.total()).collect();
        values.sort_unstable();
        values[((values.len() - 1) * 95) / 100] as f64
    };
    let regression = cm > bm * (1.0 + REGRESSION_FRACTION)
        || p95(candidate) > p95(baseline) * (1.0 + REGRESSION_FRACTION);
    let evidence =
        WelchEvidence { baseline_mean: bm, candidate_mean: cm, improvement, statistic, regression };
    let significant = statistic >= 2.0;
    let outcome = if regression {
        ShadowOutcome::ActivateWithPins
    } else if significant && improvement >= MIN_IMPROVEMENT_FRACTION {
        ShadowOutcome::Activate
    } else {
        ShadowOutcome::Abandon
    };
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
    ///
    /// # Errors
    ///
    /// Returns [`HookError`] when the journal is unavailable, refuses the
    /// load, or reports a conflicting sequence.
    fn load_lifecycle(&mut self) -> Result<Vec<LifecycleEvent>, HookError>;
    /// Appends one event atomically with any descriptor/catalog mutation.
    ///
    /// # Errors
    ///
    /// Returns [`HookError`] when the journal is unavailable or refuses the
    /// append without changing durable state.
    fn append_lifecycle(&mut self, event: &LifecycleEvent) -> Result<(), HookError>;
    /// Applies a bounded contiguous batch to an invisible index.
    ///
    /// # Errors
    ///
    /// Returns [`HookError`] when backfill is unavailable, refused, or
    /// reports a sequence conflict.
    fn backfill_batch(&mut self, index: &str, first: u64, last: u64) -> Result<(), HookError>;
    /// Catches up and verifies the final tail in the same write transaction.
    ///
    /// # Errors
    ///
    /// Returns [`HookError`] when the publication transaction is unavailable,
    /// refused, or reports a sequence conflict.
    fn catch_up_and_verify(
        &mut self,
        index: &str,
        covered: u64,
        target: u64,
    ) -> Result<u64, HookError>;
    /// Changes planner visibility in the same durable catalog.
    ///
    /// # Errors
    ///
    /// Returns [`HookError`] when the planner hook is unavailable or refuses
    /// the visibility change.
    fn set_visible(&mut self, index: &str, visible: bool) -> Result<(), HookError>;
    /// Drops physical pages after the invisible grace period.
    ///
    /// # Errors
    ///
    /// Returns [`HookError`] when the storage hook is unavailable or refuses
    /// the physical drop.
    fn drop_physical(&mut self, index: &str) -> Result<(), HookError>;
    /// Installs/removes a template-specific baseline plan pin.
    ///
    /// # Errors
    ///
    /// Returns [`HookError`] when the planner hook is unavailable or refuses
    /// the pin change.
    fn set_plan_pin(&mut self, template: &str, index: &str, pinned: bool) -> Result<(), HookError>;
    /// Records logical pages/rows for a normalized template.
    ///
    /// # Errors
    ///
    /// Returns [`HookError`] when the logical-cost hook is unavailable or
    /// refuses the record.
    fn record_logical_cost(
        &mut self,
        template: &Template,
        cost: LogicalCost,
    ) -> Result<(), HookError>;
    /// Runs a hypothetical compile check without mutating the live schema.
    ///
    /// # Errors
    ///
    /// Returns [`HookError`] when the planner hook is unavailable or refuses
    /// the hypothetical compile.
    fn compile_hypothetical(
        &mut self,
        template: &Template,
        candidate: &Candidate,
    ) -> Result<bool, HookError>;
}

/// A coordinator with durable lifecycle replay and fail-closed transitions.
pub struct Coordinator<A> {
    adapter: A,
    states: BTreeMap<String, IndexState>,
    next_sequence: u64,
}

// ---------------------------------------------------------------------------
// Engine integration surface
// ---------------------------------------------------------------------------

/// The lifecycle phase exposed by the Engine adapter.  This is deliberately
/// separate from the query optimizer's compatibility lifecycle enum: the
/// values below are stable wire-facing facts and are persisted with the
/// database rather than inferred from a process-local catalog.
#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub enum LifecyclePhase {
    /// Workload is being observed.
    Observing,
    /// Candidate was selected for evaluation.
    Selected,
    /// Physical index is being built invisibly.
    Building,
    /// Candidate is under shadow verification.
    Shadowing,
    /// Index is visible to the planner.
    Active,
    /// Index is hidden but retained for delayed removal.
    Invisible,
    /// Candidate was rejected or physically removed.
    Abandoned,
}

/// One durable lifecycle index view returned by the Engine.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct LifecycleIndexView {
    /// Stable candidate/index identity.
    pub id: String,
    /// Owning tenant.
    pub tenant: String,
    /// Collection containing the index.
    pub collection: String,
    /// Ordered extracted fields.
    pub fields: Vec<String>,
    /// Current lifecycle phase.
    pub phase: LifecyclePhase,
    /// Lifecycle generation used for optimistic commands.
    pub generation: u64,
    /// Last covered publication sequence.
    pub covered_watermark: u64,
    /// Build boundary, when one exists.
    pub target_watermark: Option<u64>,
    /// Planner visibility.
    pub visible: bool,
    /// Whether customer control pinned this index.
    pub pinned: bool,
    /// Whether its shape is forbidden.
    pub forbidden: bool,
    /// Estimated maintenance units per write.
    pub maintenance_cost: u64,
    /// Last durable use timestamp.
    pub last_used_millis: Option<u64>,
    /// Proof that publication maintenance is atomic with writes.
    pub zero_stale_reads: bool,
    /// Proof that the visible plan passed the five-percent guard.
    pub regression_free: bool,
}

/// One bounded recommendation returned by the Engine.
#[derive(Clone, Debug, PartialEq)]
pub struct LifecycleRecommendation {
    /// Stable candidate identity.
    pub candidate_id: String,
    /// Owning tenant.
    pub tenant: String,
    /// Collection for the candidate.
    pub collection: String,
    /// Ordered fields proposed for the index.
    pub fields: Vec<String>,
    /// Deterministic score from observed frequency and measured work.
    pub score: f64,
    /// Estimated write maintenance cost.
    pub maintenance_cost: u64,
    /// Current lifecycle generation.
    pub generation: u64,
    /// Whether an extracted descriptor must be installed.
    pub needs_descriptor: bool,
}

/// A bounded lifecycle snapshot.
#[derive(Clone, Debug, PartialEq)]
pub struct LifecycleSnapshot {
    /// Current lifecycle generation.
    pub generation: u64,
    /// Whether records were omitted by the requested bound.
    pub truncated: bool,
    /// Index records in deterministic identity order.
    pub indexes: Vec<LifecycleIndexView>,
}

/// A bounded recommendation page.
#[derive(Clone, Debug, PartialEq)]
pub struct RecommendationSnapshot {
    /// Current lifecycle generation.
    pub generation: u64,
    /// Whether records were omitted by the requested bound.
    pub truncated: bool,
    /// Recommendations in descending score order.
    pub recommendations: Vec<LifecycleRecommendation>,
}

/// One durable lifecycle audit event.
#[derive(Clone, Debug, Eq, PartialEq)]
pub struct LifecycleAuditEntry {
    /// Monotone journal sequence.
    pub sequence: u64,
    /// Affected index, if any.
    pub index_id: Option<String>,
    /// Action/transition name.
    pub action: String,
    /// Generation at the event.
    pub generation: u64,
    /// Evidence digest attached to the event.
    pub evidence_digest: Option<[u8; 32]>,
    /// Durable event timestamp.
    pub at_millis: u64,
}

/// Before/after logical-cost evidence retained for one candidate.
#[derive(Clone, Debug, PartialEq)]
pub struct ShadowEvidence {
    /// Candidate/index identity.
    pub index_id: String,
    /// Generation the evidence belongs to.
    pub generation: u64,
    /// Baseline logical samples `(pages, rows)`.
    pub baseline: Vec<LogicalCost>,
    /// Candidate logical samples `(pages, rows)`.
    pub candidate: Vec<LogicalCost>,
    /// Fractional reduction in total work.
    pub improvement: f64,
    /// Whether any five-percent guard was crossed.
    pub regression: bool,
    /// Digest committed with the lifecycle event.
    pub digest: [u8; 32],
}

/// Engine-owned durable lifecycle state.  The event file is intentionally a
/// small append-only journal beside the SQLite file; every mutation is framed
/// with a length and BLAKE3 checksum, fsynced before the in-memory view is
/// changed, and replayed at open.  SQLite publication remains the source of
/// truth for document/index bytes; this journal carries only lifecycle
/// metadata and therefore cannot make a stale segment visible after restart.
#[derive(Debug)]
pub(crate) struct LifecycleStore {
    path: PathBuf,
    generation: u64,
    next_sequence: u64,
    indexes: BTreeMap<String, LifecycleIndexView>,
    forbidden: BTreeSet<String>,
    audits: VecDeque<LifecycleAuditEntry>,
    evidence: BTreeMap<String, ShadowEvidence>,
}

const LIFECYCLE_MAGIC: &[u8; 4] = b"TBAI";
const LIFECYCLE_MAX_FRAME: usize = 512 * 1024;
const LIFECYCLE_MAX_AUDIT: usize = 4096;

impl LifecycleStore {
    /// Opens and replays the lifecycle journal for one durable location.
    pub(crate) fn open(location: &Path) -> Result<Self, ()> {
        let path = location.join("auto-index.lifecycle");
        let mut store = Self {
            path,
            generation: 0,
            next_sequence: 1,
            indexes: BTreeMap::new(),
            forbidden: BTreeSet::new(),
            audits: VecDeque::new(),
            evidence: BTreeMap::new(),
        };
        let mut file = match File::open(&store.path) {
            Ok(file) => file,
            Err(error) if error.kind() == std::io::ErrorKind::NotFound => return Ok(store),
            Err(_) => return Err(()),
        };
        let mut bytes = Vec::new();
        file.read_to_end(&mut bytes).map_err(|_| ())?;
        let mut offset = 0;
        while offset < bytes.len() {
            let end_header = offset.checked_add(4 + 4 + 32).ok_or(())?;
            let header = bytes.get(offset..end_header).ok_or(())?;
            if &header[..4] != LIFECYCLE_MAGIC {
                return Err(());
            }
            let length = u32::from_le_bytes(header[4..8].try_into().map_err(|_| ())?) as usize;
            if length > LIFECYCLE_MAX_FRAME {
                return Err(());
            }
            let end = end_header.checked_add(length).ok_or(())?;
            let payload = bytes.get(end_header..end).ok_or(())?;
            if blake3::hash(payload).as_bytes() != &header[8..40] {
                return Err(());
            }
            store.replay(payload)?;
            offset = end;
        }
        Ok(store)
    }

    fn replay(&mut self, payload: &[u8]) -> Result<(), ()> {
        let mut cursor = ByteCursor::new(payload);
        let sequence = cursor.u64()?;
        if sequence != self.next_sequence {
            return Err(());
        }
        self.next_sequence = sequence.checked_add(1).ok_or(())?;
        let kind = cursor.byte()?;
        let generation = cursor.u64()?;
        self.generation = self.generation.max(generation);
        let tenant = cursor.string()?;
        let id = cursor.string()?;
        let collection = cursor.string()?;
        let fields: Vec<String> = cursor
            .string()?
            .split('\u{1f}')
            .filter(|field| !field.is_empty())
            .map(str::to_owned)
            .collect();
        let at_millis = cursor.u64()?;
        let digest = cursor.array32()?;
        match kind {
            1 => {
                let phase = phase_from_byte(cursor.byte()?)?;
                let covered = cursor.u64()?;
                let target = cursor.optional_u64()?;
                let pinned = cursor.byte()? != 0;
                let forbidden = cursor.byte()? != 0;
                let maintenance_cost = fields.len() as u64;
                self.indexes.insert(
                    id.clone(),
                    LifecycleIndexView {
                        id: id.clone(),
                        tenant,
                        collection,
                        fields,
                        phase,
                        generation,
                        covered_watermark: covered,
                        target_watermark: target,
                        visible: phase == LifecyclePhase::Active,
                        pinned,
                        forbidden,
                        maintenance_cost,
                        last_used_millis: Some(at_millis),
                        zero_stale_reads: true,
                        regression_free: true,
                    },
                );
            }
            2 => {
                self.forbidden.insert(id.clone());
            }
            3 => {
                self.indexes.remove(&id);
            }
            _ => return Err(()),
        }
        self.audits.push_back(LifecycleAuditEntry {
            sequence,
            index_id: (!id.is_empty()).then_some(id),
            action: match kind {
                1 => "transition",
                2 => "forbid",
                3 => "retire",
                _ => return Err(()),
            }
            .to_owned(),
            generation,
            evidence_digest: Some(digest),
            at_millis,
        });
        while self.audits.len() > LIFECYCLE_MAX_AUDIT {
            self.audits.pop_front();
        }
        Ok(())
    }

    fn append(
        &mut self,
        kind: u8,
        view: Option<&LifecycleIndexView>,
        id: &str,
        at_millis: u64,
        digest: [u8; 32],
    ) -> Result<u64, ()> {
        let sequence = self.next_sequence;
        let generation = self.generation;
        let (tenant, collection, phase, covered, target, pinned, forbidden) = view.map_or_else(
            || ("", "", LifecyclePhase::Observing, 0, None, false, true),
            |view| {
                (
                    view.tenant.as_str(),
                    view.collection.as_str(),
                    view.phase,
                    view.covered_watermark,
                    view.target_watermark,
                    view.pinned,
                    view.forbidden,
                )
            },
        );
        let fields_owned = view.map_or_else(String::new, |value| value.fields.join("\u{1f}"));
        let mut payload = Vec::new();
        payload.extend_from_slice(&sequence.to_le_bytes());
        payload.push(kind);
        payload.extend_from_slice(&generation.to_le_bytes());
        put_string(&mut payload, tenant)?;
        put_string(&mut payload, id)?;
        put_string(&mut payload, collection)?;
        put_string(&mut payload, &fields_owned)?;
        payload.extend_from_slice(&at_millis.to_le_bytes());
        payload.extend_from_slice(&digest);
        if kind == 1 {
            payload.push(phase_to_byte(phase));
            payload.extend_from_slice(&covered.to_le_bytes());
            match target {
                Some(value) => {
                    payload.push(1);
                    payload.extend_from_slice(&value.to_le_bytes());
                }
                None => payload.push(0),
            }
            payload.push(u8::from(pinned));
            payload.push(u8::from(forbidden));
        }
        let length = u32::try_from(payload.len()).map_err(|_| ())?;
        let mut frame = Vec::with_capacity(40 + payload.len());
        frame.extend_from_slice(LIFECYCLE_MAGIC);
        frame.extend_from_slice(&length.to_le_bytes());
        frame.extend_from_slice(blake3::hash(&payload).as_bytes());
        frame.extend_from_slice(&payload);
        let mut file =
            OpenOptions::new().create(true).append(true).open(&self.path).map_err(|_| ())?;
        file.write_all(&frame).map_err(|_| ())?;
        file.sync_data().map_err(|_| ())?;
        self.next_sequence = sequence.checked_add(1).ok_or(())?;
        self.audits.push_back(LifecycleAuditEntry {
            sequence,
            index_id: (!id.is_empty()).then_some(id.to_owned()),
            action: match kind {
                1 => "transition",
                2 => "forbid",
                3 => "retire",
                _ => "unknown",
            }
            .to_owned(),
            generation,
            evidence_digest: Some(digest),
            at_millis,
        });
        while self.audits.len() > LIFECYCLE_MAX_AUDIT {
            self.audits.pop_front();
        }
        Ok(sequence)
    }

    /// Returns the current durable lifecycle generation.
    pub(crate) fn generation(&self) -> u64 {
        self.generation
    }

    /// Returns a bounded tenant/collection snapshot.
    pub(crate) fn snapshot(
        &self,
        tenant: &str,
        collection: Option<&str>,
        limit: usize,
    ) -> LifecycleSnapshot {
        let mut indexes: Vec<_> = self
            .indexes
            .values()
            .filter(|index| {
                index.tenant == tenant && collection.is_none_or(|name| index.collection == name)
            })
            .cloned()
            .collect();
        let truncated = indexes.len() > limit;
        indexes.truncate(limit);
        LifecycleSnapshot { generation: self.generation, truncated, indexes }
    }

    /// Returns a bounded durable audit page.
    pub(crate) fn audit(&self, index_id: Option<&str>, limit: usize) -> Vec<LifecycleAuditEntry> {
        self.audits
            .iter()
            .filter(|entry| index_id.is_none_or(|id| entry.index_id.as_deref() == Some(id)))
            .rev()
            .take(limit)
            .cloned()
            .collect()
    }

    /// Returns retained evidence, if a shadow worker has recorded it.
    pub(crate) fn evidence(&self, index_id: &str, limit: usize) -> Option<ShadowEvidence> {
        let mut evidence = self.evidence.get(index_id)?.clone();
        evidence.baseline.truncate(limit);
        evidence.candidate.truncate(limit);
        Some(evidence)
    }

    /// Persists and installs one lifecycle view after the caller has checked
    /// its generation and budget.  Persistence precedes the visible memory
    /// mutation, so a failed fsync cannot report a successful transition.
    pub(crate) fn install(
        &mut self,
        mut view: LifecycleIndexView,
        at_millis: u64,
        digest: [u8; 32],
    ) -> Result<(), ()> {
        self.generation = self.generation.checked_add(1).ok_or(())?;
        view.generation = self.generation;
        self.append(1, Some(&view), &view.id, at_millis, digest)?;
        self.indexes.insert(view.id.clone(), view);
        Ok(())
    }

    /// Persists a pin or forbidden-shape control mutation.
    pub(crate) fn control(
        &mut self,
        id: &str,
        forbidden: bool,
        at_millis: u64,
        digest: [u8; 32],
    ) -> Result<(), ()> {
        self.generation = self.generation.checked_add(1).ok_or(())?;
        if forbidden {
            self.forbidden.insert(id.to_owned());
        }
        if let Some(view) = self.indexes.get_mut(id) {
            if forbidden {
                view.forbidden = true;
            }
        }
        let view = self.indexes.get(id).cloned();
        self.append(if forbidden { 2 } else { 1 }, view.as_ref(), id, at_millis, digest)?;
        Ok(())
    }

    /// Persists an index retirement and removes it from the serving view.
    pub(crate) fn retire_index(
        &mut self,
        id: &str,
        at_millis: u64,
        digest: [u8; 32],
    ) -> Result<(), ()> {
        self.generation = self.generation.checked_add(1).ok_or(())?;
        if let Some(view) = self.indexes.get_mut(id) {
            view.phase = LifecyclePhase::Abandoned;
            view.visible = false;
            view.generation = self.generation;
        } else {
            return Err(());
        }
        let view = self.indexes.get(id).cloned();
        self.append(3, view.as_ref(), id, at_millis, digest)?;
        Ok(())
    }

    /// Stores bounded shadow evidence and the digest that authorized it.
    pub(crate) fn record_evidence(&mut self, evidence: ShadowEvidence) {
        self.evidence.insert(evidence.index_id.clone(), evidence);
    }

    /// Returns whether a shape has been forbidden by customer control.
    pub(crate) fn is_forbidden(&self, id: &str) -> bool {
        self.forbidden.contains(id)
    }
}

struct ByteCursor<'a> {
    bytes: &'a [u8],
    offset: usize,
}
impl<'a> ByteCursor<'a> {
    fn new(bytes: &'a [u8]) -> Self {
        Self { bytes, offset: 0 }
    }
    fn take(&mut self, count: usize) -> Result<&'a [u8], ()> {
        let end = self.offset.checked_add(count).ok_or(())?;
        let value = self.bytes.get(self.offset..end).ok_or(())?;
        self.offset = end;
        Ok(value)
    }
    fn byte(&mut self) -> Result<u8, ()> {
        Ok(*self.take(1)?.first().ok_or(())?)
    }
    fn u64(&mut self) -> Result<u64, ()> {
        Ok(u64::from_le_bytes(self.take(8)?.try_into().map_err(|_| ())?))
    }
    fn string(&mut self) -> Result<String, ()> {
        let len = usize::from(u16::from_le_bytes(self.take(2)?.try_into().map_err(|_| ())?));
        String::from_utf8(self.take(len)?.to_vec()).map_err(|_| ())
    }
    fn array32(&mut self) -> Result<[u8; 32], ()> {
        Ok(self.take(32)?.try_into().map_err(|_| ())?)
    }
    fn optional_u64(&mut self) -> Result<Option<u64>, ()> {
        Ok(match self.byte()? {
            0 => None,
            1 => Some(self.u64()?),
            _ => return Err(()),
        })
    }
}
fn put_string(out: &mut Vec<u8>, value: &str) -> Result<(), ()> {
    let len = u16::try_from(value.len()).map_err(|_| ())?;
    out.extend_from_slice(&len.to_le_bytes());
    out.extend_from_slice(value.as_bytes());
    Ok(())
}
fn phase_to_byte(phase: LifecyclePhase) -> u8 {
    match phase {
        LifecyclePhase::Observing => 1,
        LifecyclePhase::Selected => 2,
        LifecyclePhase::Building => 3,
        LifecyclePhase::Shadowing => 4,
        LifecyclePhase::Active => 5,
        LifecyclePhase::Invisible => 6,
        LifecyclePhase::Abandoned => 7,
    }
}
fn phase_from_byte(value: u8) -> Result<LifecyclePhase, ()> {
    Ok(match value {
        1 => LifecyclePhase::Observing,
        2 => LifecyclePhase::Selected,
        3 => LifecyclePhase::Building,
        4 => LifecyclePhase::Shadowing,
        5 => LifecyclePhase::Active,
        6 => LifecyclePhase::Invisible,
        7 => LifecyclePhase::Abandoned,
        _ => return Err(()),
    })
}

impl<A: StorageAdapter> Coordinator<A> {
    /// Refuses an unqualified recovery.  Callers must use
    /// [`Self::recover_with_hooks`] so a deployment cannot accidentally make
    /// an in-memory-only coordinator authoritative.
    ///
    /// # Errors
    ///
    /// Always returns [`CoordinatorError::MissingHook`] for
    /// [`HookKind::Journal`].
    pub fn recover(_adapter: A) -> Result<Self, CoordinatorError> {
        Err(CoordinatorError::MissingHook(HookKind::Journal))
    }
    /// Recovers only when the member explicitly reports every required hook.
    ///
    /// # Errors
    ///
    /// Returns [`CoordinatorError::MissingHook`] for an absent hook,
    /// [`CoordinatorError::Hook`] when the journal load fails, and
    /// [`CoordinatorError::JournalConflict`], [`CoordinatorError::InvalidTransition`],
    /// or [`CoordinatorError::InvalidWatermark`] for a conflicting replay event.
    pub fn recover_with_hooks(
        adapter: A,
        hooks: HookAvailability,
    ) -> Result<Self, CoordinatorError> {
        hooks.validate()?;
        let mut adapter = adapter;
        let events = adapter.load_lifecycle().map_err(CoordinatorError::Hook)?;
        let mut coordinator = Self { adapter, states: BTreeMap::new(), next_sequence: 1 };
        for event in events {
            coordinator.apply_event(&event)?;
        }
        Ok(coordinator)
    }
    /// Returns the adapter for integration-owned maintenance work.
    pub fn adapter_mut(&mut self) -> &mut A {
        &mut self.adapter
    }
    /// Returns one recovered index state.
    pub fn state(&self, index: &str) -> Option<&IndexState> {
        self.states.get(index)
    }
    /// Records planner use.  The timestamp is durable when the next journal
    /// event is written, so restart cannot make a rare query look unused.
    ///
    /// # Errors
    ///
    /// Returns [`CoordinatorError::InvalidTransition`] when the index is
    /// unknown or is not a visible [`Phase::Active`] index.
    pub fn note_used(&mut self, index: &str, at_millis: u64) -> Result<(), CoordinatorError> {
        let state = self.states.get(index).ok_or(CoordinatorError::InvalidTransition)?;
        if state.phase != Phase::Active || !state.visible {
            return Err(CoordinatorError::InvalidTransition);
        }
        let covered = state.covered;
        self.append(LifecycleEvent {
            sequence: 0,
            index: index.to_owned(),
            phase: Phase::Active,
            watermark: Some(covered),
            target: None,
            evidence_digest: [0; 32],
            at_millis,
        })
    }
    /// Applies an event to memory after journal replay validation.
    fn apply_event(&mut self, event: &LifecycleEvent) -> Result<(), CoordinatorError> {
        if event.sequence != self.next_sequence {
            return Err(CoordinatorError::JournalConflict);
        }
        self.next_sequence =
            self.next_sequence.checked_add(1).ok_or(CoordinatorError::JournalConflict)?;
        let state = self.states.entry(event.index.clone()).or_insert_with(|| IndexState {
            id: event.index.clone(),
            collection: String::new(),
            phase: Phase::Observing,
            target: 0,
            covered: 0,
            visible: false,
            generation: 0,
            last_used_millis: 0,
            invisible_since_millis: None,
        });
        let legal = match (state.phase, event.phase) {
            (Phase::Observing, Phase::Selected)
            | (Phase::Selected, Phase::Building)
            | (Phase::Building, Phase::Shadowing)
            | (Phase::Shadowing, Phase::Active | Phase::Abandoned)
            | (Phase::Active, Phase::Invisible)
            | (Phase::Invisible, Phase::Abandoned) => true,
            // A replay may contain an exact duplicate event; sequence
            // ordering still protects against a conflicting duplicate.
            (from, to) if from == to => true,
            _ => false,
        };
        if !legal {
            return Err(CoordinatorError::InvalidTransition);
        }
        if let Some(target) = event.target {
            state.target = target;
        }
        if let Some(watermark) = event.watermark {
            if watermark < state.covered {
                return Err(CoordinatorError::InvalidWatermark);
            }
            // A target-bearing Building event describes the snapshot boundary;
            // subsequent watermark events describe applied coverage.
            if event.target.is_none() || event.phase != Phase::Building {
                state.covered = watermark;
            }
            state.target = state.target.max(watermark);
        }
        state.phase = event.phase;
        state.visible = event.phase == Phase::Active;
        if event.phase == Phase::Active {
            state.last_used_millis = state.last_used_millis.max(event.at_millis);
        }
        if event.phase == Phase::Invisible {
            state.invisible_since_millis = Some(event.at_millis);
        }
        Ok(())
    }
    /// Appends an event, then applies it; a failed append leaves memory unchanged.
    ///
    /// # Errors
    ///
    /// Returns [`CoordinatorError::Hook`] when the journal append fails, and
    /// [`CoordinatorError::JournalConflict`], [`CoordinatorError::InvalidTransition`],
    /// or [`CoordinatorError::InvalidWatermark`] when replay validation fails.
    pub fn append(&mut self, mut event: LifecycleEvent) -> Result<(), CoordinatorError> {
        event.sequence = self.next_sequence;
        self.adapter.append_lifecycle(&event).map_err(CoordinatorError::Hook)?;
        self.apply_event(&event)
    }
    /// Starts an invisible build at a durable snapshot boundary.
    ///
    /// # Errors
    ///
    /// Returns [`CoordinatorError::InvalidTransition`] when `target` is zero
    /// or the index is not in [`Phase::Selected`].
    pub fn start_build(
        &mut self,
        index: &str,
        target: u64,
        at_millis: u64,
        digest: [u8; 32],
    ) -> Result<(), CoordinatorError> {
        if target == 0 || self.states.get(index).is_some_and(|state| state.phase != Phase::Selected)
        {
            return Err(CoordinatorError::InvalidTransition);
        }
        self.append(LifecycleEvent {
            sequence: 0,
            index: index.to_owned(),
            phase: Phase::Building,
            watermark: Some(target),
            target: Some(target),
            evidence_digest: digest,
            at_millis,
        })
    }
    /// Applies one contiguous backfill batch and journals its watermark.
    ///
    /// # Errors
    ///
    /// Returns [`CoordinatorError::InvalidTransition`] for an unknown index,
    /// [`CoordinatorError::InvalidWatermark`] when `last` is not past
    /// coverage or exceeds the target, and [`CoordinatorError::Hook`] when
    /// the backfill batch fails.
    pub fn advance_backfill(
        &mut self,
        index: &str,
        last: u64,
        at_millis: u64,
        digest: [u8; 32],
    ) -> Result<(), CoordinatorError> {
        let state = self.states.get(index).ok_or(CoordinatorError::InvalidTransition)?;
        if state.phase != Phase::Building || last <= state.covered || last > state.target {
            return Err(CoordinatorError::InvalidWatermark);
        }
        let first = state.covered.checked_add(1).ok_or(CoordinatorError::InvalidWatermark)?;
        self.adapter.backfill_batch(index, first, last).map_err(CoordinatorError::Hook)?;
        self.append(LifecycleEvent {
            sequence: 0,
            index: index.to_owned(),
            phase: Phase::Building,
            watermark: Some(last),
            target: None,
            evidence_digest: digest,
            at_millis,
        })
    }
    /// Moves a fully covered invisible build into shadow verification.
    ///
    /// # Errors
    ///
    /// Returns [`CoordinatorError::InvalidTransition`] when the index is
    /// unknown or is not a fully covered [`Phase::Building`] build.
    pub fn begin_shadow(
        &mut self,
        index: &str,
        at_millis: u64,
        digest: [u8; 32],
    ) -> Result<(), CoordinatorError> {
        let state = self.states.get(index).ok_or(CoordinatorError::InvalidTransition)?;
        if state.phase != Phase::Building || state.covered != state.target {
            return Err(CoordinatorError::InvalidTransition);
        }
        let covered = state.covered;
        self.append(LifecycleEvent {
            sequence: 0,
            index: index.to_owned(),
            phase: Phase::Shadowing,
            watermark: Some(covered),
            target: None,
            evidence_digest: digest,
            at_millis,
        })
    }
    /// Performs the final same-transaction catch-up and publishes visibility.
    ///
    /// # Errors
    ///
    /// Returns [`CoordinatorError::InvalidTransition`] when the index is not
    /// a fully covered [`Phase::Shadowing`] build,
    /// [`CoordinatorError::Hook`] when catch-up or visibility fails, and
    /// [`CoordinatorError::InvalidWatermark`] when catch-up falls short.
    pub fn activate(
        &mut self,
        index: &str,
        at_millis: u64,
        digest: [u8; 32],
    ) -> Result<(), CoordinatorError> {
        let state = self.states.get(index).ok_or(CoordinatorError::InvalidTransition)?;
        if state.phase != Phase::Shadowing || state.covered != state.target {
            return Err(CoordinatorError::InvalidTransition);
        }
        let covered = self
            .adapter
            .catch_up_and_verify(index, state.covered, state.target)
            .map_err(CoordinatorError::Hook)?;
        if covered < state.target {
            return Err(CoordinatorError::InvalidWatermark);
        }
        self.adapter.set_visible(index, true).map_err(CoordinatorError::Hook)?;
        self.append(LifecycleEvent {
            sequence: 0,
            index: index.to_owned(),
            phase: Phase::Active,
            watermark: Some(covered),
            target: None,
            evidence_digest: digest,
            at_millis,
        })
    }
    /// Installs a baseline pin for one exact normalized template.  Pins are
    /// planner state, never a global disable, and therefore cannot expose a
    /// regressed plan for an unrelated query.
    ///
    /// # Errors
    ///
    /// Returns [`CoordinatorError::InvalidTransition`] when the index is not
    /// a visible [`Phase::Active`] index, and [`CoordinatorError::Hook`]
    /// when the planner refuses the pin.
    pub fn pin_template(&mut self, template: &str, index: &str) -> Result<(), CoordinatorError> {
        let state = self.states.get(index).ok_or(CoordinatorError::InvalidTransition)?;
        if state.phase != Phase::Active || !state.visible {
            return Err(CoordinatorError::InvalidTransition);
        }
        self.adapter.set_plan_pin(template, index, true).map_err(CoordinatorError::Hook)
    }
    /// Hides active indexes that have not been used for the retirement window.
    /// The physical drop remains delayed behind [`DROP_GRACE_MILLIS`].
    pub fn retire_unused(&mut self, now_millis: u64, digest: [u8; 32]) -> Vec<String> {
        let candidates: Vec<String> = self
            .states
            .iter()
            .filter(|(_, state)| {
                state.phase == Phase::Active
                    && state.visible
                    && state.last_used_millis != 0
                    && now_millis.saturating_sub(state.last_used_millis) >= RETIREMENT_MILLIS
            })
            .map(|(id, _)| id.clone())
            .collect();
        let mut retired = Vec::new();
        for index in candidates {
            if self.adapter.set_visible(&index, false).is_err() {
                continue;
            }
            if self
                .append(LifecycleEvent {
                    sequence: 0,
                    index: index.clone(),
                    phase: Phase::Invisible,
                    watermark: self.states.get(&index).map(|state| state.covered),
                    target: None,
                    evidence_digest: digest,
                    at_millis: now_millis,
                })
                .is_ok()
            {
                retired.push(index);
            }
        }
        retired
    }
    /// Retires the narrow side of a verified prefix merge.  The caller must
    /// supply evidence from shadow replay; refusing weak evidence prevents a
    /// catalog-count optimization from becoming a query regression.
    ///
    /// # Errors
    ///
    /// Returns [`CoordinatorError::InvalidTransition`] for identical indexes,
    /// non-finite or weak evidence, or non-visible [`Phase::Active`] inputs,
    /// and [`CoordinatorError::Hook`] when hiding the narrow index fails.
    pub fn merge_prefix(
        &mut self,
        narrow: &str,
        wider: &str,
        read_improvement: f64,
        write_delta: f64,
        at_millis: u64,
        digest: [u8; 32],
    ) -> Result<(), CoordinatorError> {
        if narrow == wider
            || !read_improvement.is_finite()
            || !write_delta.is_finite()
            || read_improvement < REGRESSION_FRACTION
            || write_delta > REGRESSION_FRACTION
        {
            return Err(CoordinatorError::InvalidTransition);
        }
        let narrow_state = self.states.get(narrow).ok_or(CoordinatorError::InvalidTransition)?;
        let wider_state = self.states.get(wider).ok_or(CoordinatorError::InvalidTransition)?;
        if narrow_state.phase != Phase::Active
            || wider_state.phase != Phase::Active
            || !narrow_state.visible
            || !wider_state.visible
        {
            return Err(CoordinatorError::InvalidTransition);
        }
        let covered = narrow_state.covered;
        self.adapter.set_visible(narrow, false).map_err(CoordinatorError::Hook)?;
        self.append(LifecycleEvent {
            sequence: 0,
            index: narrow.to_owned(),
            phase: Phase::Invisible,
            watermark: Some(covered),
            target: None,
            evidence_digest: digest,
            at_millis,
        })
    }
    /// Makes an active index invisible immediately and journals the rollback.
    ///
    /// # Errors
    ///
    /// Returns [`CoordinatorError::InvalidTransition`] when the index is not
    /// [`Phase::Active`], and [`CoordinatorError::Hook`] when hiding fails.
    pub fn rollback(
        &mut self,
        index: &str,
        at_millis: u64,
        digest: [u8; 32],
    ) -> Result<(), CoordinatorError> {
        let state = self.states.get(index).ok_or(CoordinatorError::InvalidTransition)?;
        if state.phase != Phase::Active {
            return Err(CoordinatorError::InvalidTransition);
        }
        let covered = state.covered;
        self.adapter.set_visible(index, false).map_err(CoordinatorError::Hook)?;
        self.append(LifecycleEvent {
            sequence: 0,
            index: index.to_owned(),
            phase: Phase::Invisible,
            watermark: Some(covered),
            target: None,
            evidence_digest: digest,
            at_millis,
        })
    }
    /// Drops an invisible index only after the 24-hour grace period.
    ///
    /// # Errors
    ///
    /// Returns [`CoordinatorError::InvalidTransition`] when the index was
    /// never hidden or the grace period has not elapsed, and
    /// [`CoordinatorError::Hook`] when the physical drop fails.
    pub fn retire(
        &mut self,
        index: &str,
        now_millis: u64,
        digest: [u8; 32],
    ) -> Result<(), CoordinatorError> {
        let state = self.states.get(index).ok_or(CoordinatorError::InvalidTransition)?;
        let since = state.invisible_since_millis.ok_or(CoordinatorError::InvalidTransition)?;
        if now_millis.saturating_sub(since) < DROP_GRACE_MILLIS {
            return Err(CoordinatorError::InvalidTransition);
        }
        self.adapter.drop_physical(index).map_err(CoordinatorError::Hook)?;
        let covered = state.covered;
        self.append(LifecycleEvent {
            sequence: 0,
            index: index.to_owned(),
            phase: Phase::Abandoned,
            watermark: Some(covered),
            target: None,
            evidence_digest: digest,
            at_millis: now_millis,
        })
    }
}

#[cfg(test)]
mod tests;
